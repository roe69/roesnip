using System;
using System.Xml;

namespace RoeSnip.Core.Updates;

/// <summary>Reads which release the installed unit already is, so an updater that got the update
/// lock after another one finished does not swap the same release in again (that second swap
/// would push the build just launched into ".old" and delete the real previous build).</summary>
public static class InstalledVersion
{
    /// <summary>True when <paramref name="installed"/> ("2.7.1", "2.7.1.0", "2.7.1+sha") names the
    /// same major.minor.patch as <paramref name="release"/>. Null, unparsable or 0.0.0 is false.</summary>
    public static bool Matches(string? installed, Version release)
    {
        if (string.IsNullOrWhiteSpace(installed))
        {
            return false;
        }

        string text = installed.Trim();
        int cut = text.IndexOfAny(new[] { '+', '-', ' ' });
        if (cut >= 0)
        {
            text = text[..cut];
        }

        return Version.TryParse(text, out Version? parsed) && Matches(parsed, release);
    }

    /// <summary>Major.minor.patch equality (a missing patch counts as 0); 0.0.0 never matches.</summary>
    public static bool Matches(Version installed, Version release)
    {
        if (installed.Major == 0 && installed.Minor == 0 && Math.Max(installed.Build, 0) == 0)
        {
            return false;
        }

        return installed.Major == release.Major &&
               installed.Minor == release.Minor &&
               Math.Max(installed.Build, 0) == Math.Max(release.Build, 0);
    }

    /// <summary>CFBundleShortVersionString from an XML Info.plist (the form release.yml writes);
    /// null when absent or not XML (a binary plist).</summary>
    public static string? FromMacInfoPlist(string plistXml)
    {
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using (var reader = XmlReader.Create(new System.IO.StringReader(plistXml), settings))
            {
                doc.Load(reader);
            }

            XmlNodeList? keys = doc.SelectNodes("/plist/dict/key");
            if (keys is null)
            {
                return null;
            }

            foreach (XmlNode key in keys)
            {
                if (key.InnerText.Trim() != "CFBundleShortVersionString")
                {
                    continue;
                }

                XmlNode? value = key.NextSibling;
                while (value is not null && value.NodeType != XmlNodeType.Element)
                {
                    value = value.NextSibling;
                }

                return value is { Name: "string" } ? value.InnerText.Trim() : null;
            }

            return null;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
