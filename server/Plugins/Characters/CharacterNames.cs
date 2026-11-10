using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cyberverse.Plugin.Characters;

/// <summary>"Firstname Lastname": each part 2 to 16 letters plus ' and -, starting with a letter (CYB-00009).</summary>
public static partial class CharacterNames
{
    public const int MinLength = 2;
    public const int MaxLength = 16;

    [GeneratedRegex(@"^\p{L}[\p{L}\p{M}'\-]*$")]
    private static partial Regex Part();

    /// <summary>The trimmed, NFC-normalized parts, or a message for the player when they don't follow the rules.</summary>
    public static bool TryNormalize(string first, string last, out string firstName, out string lastName, out string error)
    {
        firstName = first.Trim().Normalize(NormalizationForm.FormC);
        lastName = last.Trim().Normalize(NormalizationForm.FormC);
        error = Check(firstName, "first name") ?? Check(lastName, "last name") ?? "";
        return error.Length == 0;
    }

    private static string? Check(string part, string what)
    {
        var length = new StringInfo(part).LengthInTextElements;
        if (length < MinLength || length > MaxLength)
        {
            return $"The {what} must be {MinLength} to {MaxLength} letters long.";
        }

        return Part().IsMatch(part) ? null : $"The {what} may only have letters, ' and -, and must start with a letter.";
    }

    /// <summary>What makes two names the same: case doesn't count.</summary>
    public static string Key(string firstName, string lastName) =>
        $"{firstName} {lastName}".ToLower(CultureInfo.InvariantCulture);
}
