using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Chalk.Sample.Blotter.Grid;

/// <summary>
///     Shrouds the values a view's link carries (its filters' values, and the row it is held at), so
///     that the link shows none of them: encrypted and signed with ASP.NET Data Protection, by keys the
///     host keeps in a folder, so that a link still opens after the host restarts. The host alone can
///     read a shrouded value, and one altered is refused.
/// </summary>
public sealed class LinkShroud(IDataProtectionProvider provider)
{
    // Its purpose ties a key to view links: a value shrouded for anything else does not open here.
    private readonly IDataProtector _protector = provider.CreateProtector("Chalk.Sample.Blotter.ViewLink.v1");

    /// <summary>Text, shrouded: URL-safe, and different every time, though it opens the same.</summary>
    public string Shroud(string text)
    {
        return _protector.Protect(text);
    }

    /// <summary>The text a value shrouds.</summary>
    /// <exception cref="ArgumentException">The value was altered, or shrouded by keys the host does not have.</exception>
    public string Unshroud(string shrouded)
    {
        try
        {
            return _protector.Unprotect(shrouded);
        }
        catch (CryptographicException)
        {
            throw new ArgumentException(
                "the link is not a view of the blotter: its shrouded part was altered, or shrouded by another blotter's keys");
        }
    }
}