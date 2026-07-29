using System.Globalization;

namespace LedController.Core.Models;

public sealed class LedColor
{
    private const string CommandPrefix = "7e000503";
    private const string CommandSuffix = "00ef";

    public static LedColor Off { get; } = new("Off", "#000000");

    public static byte[] OffCommandBytes => Off.ToCommandBytes();

    public static LedColor[] Defaults { get; } =
    {
        new("Piros", "#ff0000"),
        new("Zöld", "#00ff00"),
        new("Kék", "#0000ff"),
        new("Sárga", "#ffff00"),
        new("Cián", "#00ffff"),
        new("Lila", "#800080"),
        new("Narancs", "#ffa500"),
        new("Fehér", "#ffffff"),
    };

    public LedColor(string name, string hex)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Hex = hex ?? throw new ArgumentNullException(nameof(hex));
    }

    public string Name { get; }

    public string Hex { get; }

    public string NormalizedHex => NormalizeHex(Hex);

    public byte[] ToCommandBytes()
    {
        var hexValue = NormalizeHex(Hex).TrimStart('#');
        if (hexValue.Length != 6)
        {
            throw new ArgumentException("Hex color must be 6 characters (RRGGBB).", nameof(Hex));
        }

        var commandHex = $"{CommandPrefix}{hexValue}{CommandSuffix}";
        return HexToBytes(commandHex);
    }

    private static string NormalizeHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            throw new ArgumentException("Hex color cannot be empty.", nameof(hex));
        }

        var trimmed = hex.Trim();
        if (!trimmed.StartsWith('#'))
        {
            trimmed = $"#{trimmed}";
        }

        return trimmed.ToLowerInvariant();
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            throw new ArgumentException("Hex string must have an even length.", nameof(hex));
        }

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var slice = hex.Substring(i * 2, 2);
            bytes[i] = byte.Parse(slice, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }
}
