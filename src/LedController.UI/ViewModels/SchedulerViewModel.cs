using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedController.Core.Interfaces;
using LedController.Core.Models;

namespace LedController.UI.ViewModels;

public sealed partial class SchedulerViewModel : ViewModelBase, IDisposable
{
    private readonly IConfigService _configService;
    private readonly ILocationService _locationService;
    private readonly ISchedulerService _schedulerService;
    private readonly SemaphoreSlim _activationSaveGate = new(1, 1);
    private bool _suppressActivationSave;
    private bool _pendingActivationSave;

    public SchedulerViewModel(
        IConfigService configService,
        ISchedulerService schedulerService,
        ILocationService locationService,
        LedDevice targetDevice)
    {
        _configService = configService;
        _schedulerService = schedulerService;
        _locationService = locationService;
        TargetDevice = targetDevice ?? throw new ArgumentNullException(nameof(targetDevice));

        Profiles = new ObservableCollection<ScheduleProfile>();
        AvailableColors = new ObservableCollection<LedColor>(LedColor.Defaults);
        DailySchedules = new ObservableCollection<DailySchedule>();

        Profiles.CollectionChanged += OnProfilesCollectionChanged;

        _ = LoadAsync();
    }

    public LedDevice TargetDevice { get; }

    public ObservableCollection<ScheduleProfile> Profiles { get; }

    public ObservableCollection<LedColor> AvailableColors { get; }

    public event Action<LedDevice>? BackRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    private ScheduleProfile? selectedProfile;

    [ObservableProperty]
    private ObservableCollection<DailySchedule> dailySchedules;

    [ObservableProperty]
    private GeoCoordinate? timelineCoordinates;

    [RelayCommand]
    private void Back()
    {
        BackRequested?.Invoke(TargetDevice);
    }

    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        try
        {
            var config = await _configService.LoadConfigAsync();
            var devices = config.SavedDevices.ToList();
            var target = FindDevice(devices, TargetDevice);
            if (target is null)
            {
                target = TargetDevice;
                devices.Add(target);
            }

            target.Profiles = new ObservableCollection<ScheduleProfile>(Profiles);
            TargetDevice.Profiles = target.Profiles;

            var updatedConfig = new AppConfig(devices, config.Profiles, config.Settings);
            await _configService.SaveConfigAsync(updatedConfig);

            var legacyPath = _legacySchedulePath ?? ResolveDefaultLegacyPath();
            if (!string.IsNullOrWhiteSpace(legacyPath))
            {
                await WriteLegacyScheduleAsync(legacyPath);
                _legacySchedulePath ??= legacyPath;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scheduler] Save failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void AddProfile()
    {
        var baseName = "Új profil";
        var name = baseName;
        var index = 1;
        while (Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} {index}";
            index++;
        }

        var defaultColor = AvailableColors.FirstOrDefault() ?? LedColor.Off;
        var profile = CreateDefaultProfile(name, defaultColor);

        Profiles.Add(profile);
        SelectedProfile = profile;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private void DeleteProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var removed = SelectedProfile;
        Profiles.Remove(removed);
        SelectedProfile = Profiles.FirstOrDefault();

        DeleteProfileCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
    }

    private bool CanSaveProfile() => SelectedProfile is not null;

    private bool CanDeleteProfile() => SelectedProfile is not null && Profiles.Count > 1;

    partial void OnSelectedProfileChanged(ScheduleProfile? value)
    {
        if (value is null)
        {
            DailySchedules = new ObservableCollection<DailySchedule>();
            return;
        }

        EnsureDailySchedules(value);
        AlignColors(value);
        DailySchedules = value.DailySchedules;
    }

    private async Task LoadAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            _suppressActivationSave = true;
            Profiles.Clear();

            var device = FindDevice(config.SavedDevices, TargetDevice);
            var sourceProfiles = device?.Profiles ?? new ObservableCollection<ScheduleProfile>();

            if (sourceProfiles.Count == 0 && config.Profiles.Count > 0)
            {
                sourceProfiles = new ObservableCollection<ScheduleProfile>(
                    config.Profiles.Select(CloneProfile));
            }

            if (sourceProfiles.Count == 0)
            {
                var defaultColor = AvailableColors.FirstOrDefault() ?? LedColor.Off;
                sourceProfiles.Add(CreateDefaultProfile("Alapértelmezett", defaultColor));
            }

            foreach (var profile in sourceProfiles)
            {
                EnsureDailySchedules(profile);
                AlignColors(profile);
                Profiles.Add(profile);
            }

            SelectedProfile = Profiles.FirstOrDefault();
            _suppressActivationSave = false;
            await LoadTimelineCoordinatesAsync(config);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scheduler] Load failed: {ex.Message}");
        }
    }

    private async Task LoadTimelineCoordinatesAsync(AppConfig config)
    {
        try
        {
            var settings = config.Settings ?? AppSettings.Default;
            var coords = new GeoCoordinate(settings.Latitude, settings.Longitude);
            if (coords.Latitude == 0 && coords.Longitude == 0)
            {
                coords = await _locationService.GetCurrentLocationAsync();
            }

            TimelineCoordinates = coords;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scheduler] Location resolve failed: {ex.Message}");
            var fallback = AppSettings.Default;
            TimelineCoordinates = new GeoCoordinate(fallback.Latitude, fallback.Longitude);
        }
    }

    private void AlignColors(ScheduleProfile profile)
    {
        foreach (var schedule in profile.DailySchedules)
        {
            if (schedule.TargetColor is null)
            {
                schedule.TargetColor = AvailableColors.FirstOrDefault() ?? LedColor.Off;
                continue;
            }

            var match = AvailableColors.FirstOrDefault(c =>
                string.Equals(c.NormalizedHex, schedule.TargetColor.NormalizedHex, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                AvailableColors.Add(schedule.TargetColor);
            }
            else
            {
                schedule.TargetColor = match;
            }
        }
    }

    private static void EnsureDailySchedules(ScheduleProfile profile)
    {
        var ordered = new List<DailySchedule>();

        foreach (var day in GetWeekOrder())
        {
            var existing = profile.DailySchedules.FirstOrDefault(d => d.DayOfWeek == day);
            if (existing is null)
            {
                existing = new DailySchedule
                {
                    DayOfWeek = day
                };
            }

            ordered.Add(existing);
        }

        profile.DailySchedules = new ObservableCollection<DailySchedule>(ordered);
    }

    private static ScheduleProfile CreateDefaultProfile(string name, LedColor defaultColor)
    {
        var profile = new ScheduleProfile
        {
            Name = name,
            IsActive = true
        };

        foreach (var day in GetWeekOrder())
        {
            profile.DailySchedules.Add(new DailySchedule
            {
                DayOfWeek = day,
                TargetColor = defaultColor
            });
        }

        return profile;
    }

    private static ScheduleProfile CloneProfile(ScheduleProfile source)
    {
        var clone = new ScheduleProfile
        {
            Name = source.Name,
            IsActive = source.IsActive
        };

        foreach (var schedule in source.DailySchedules)
        {
            clone.DailySchedules.Add(new DailySchedule
            {
                DayOfWeek = schedule.DayOfWeek,
                SunriseEnabled = schedule.SunriseEnabled,
                SunriseTurnsOn = schedule.SunriseTurnsOn,
                SunriseOffset = schedule.SunriseOffset,
                SunsetEnabled = schedule.SunsetEnabled,
                SunsetTurnsOn = schedule.SunsetTurnsOn,
                SunsetOffset = schedule.SunsetOffset,
                FixedOnTime = schedule.FixedOnTime,
                FixedOffTime = schedule.FixedOffTime,
                TargetColor = schedule.TargetColor is null
                    ? null
                    : new LedColor(schedule.TargetColor.Name, schedule.TargetColor.Hex)
            });
        }

        return clone;
    }

    private static LedDevice? FindDevice(IReadOnlyList<LedDevice> devices, LedDevice target)
    {
        return devices.FirstOrDefault(d => LedDeviceIdentity.Matches(d, target));
    }

    private static DayOfWeek[] GetWeekOrder() => new[]
    {
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday
    };

    private void OnProfilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var profile in e.OldItems.OfType<ScheduleProfile>())
            {
                profile.PropertyChanged -= OnProfilePropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var profile in e.NewItems.OfType<ScheduleProfile>())
            {
                profile.PropertyChanged += OnProfilePropertyChanged;
            }
        }

        DeleteProfileCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
    }

    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScheduleProfile.IsActive))
        {
            _ = SaveActivationStateAsync();
        }
    }

    private async Task SaveActivationStateAsync()
    {
        if (_suppressActivationSave)
        {
            return;
        }

        if (!_activationSaveGate.Wait(0))
        {
            _pendingActivationSave = true;
            return;
        }

        try
        {
            do
            {
                _pendingActivationSave = false;

                var config = await _configService.LoadConfigAsync();
                var devices = config.SavedDevices.ToList();
                var target = FindDevice(devices, TargetDevice);
                if (target is null)
                {
                    target = TargetDevice;
                    devices.Add(target);
                }

                var uiProfiles = Profiles.ToList();
                var updateTargetProfiles = target.Profiles.Count > 0;
                var profilesToUpdate = updateTargetProfiles ? target.Profiles : config.Profiles;

                UpdateProfileActivation(profilesToUpdate, uiProfiles);

                var updatedConfig = new AppConfig(devices, config.Profiles, config.Settings);
                await _configService.SaveConfigAsync(updatedConfig);
                _schedulerService.MarkDeviceStateDirty(TargetDevice.Id);
            }
            while (_pendingActivationSave);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Scheduler] Activation save failed: {ex.Message}");
        }
        finally
        {
            _activationSaveGate.Release();
        }
    }

    private static void UpdateProfileActivation(
        IReadOnlyList<ScheduleProfile> profilesToUpdate,
        IReadOnlyList<ScheduleProfile> uiProfiles)
    {
        foreach (var profile in profilesToUpdate)
        {
            var match = uiProfiles.FirstOrDefault(p =>
                string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                profile.IsActive = match.IsActive;
            }
        }
    }

    public void Dispose()
    {
        Profiles.CollectionChanged -= OnProfilesCollectionChanged;

        foreach (var profile in Profiles)
        {
            profile.PropertyChanged -= OnProfilePropertyChanged;
        }
    }

}
