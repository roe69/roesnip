using RoeSnip.Core.Updates;
using Xunit;

namespace RoeSnip.Core.Tests.Updates;

/// <summary>How the portals learn RoeSnip's app id on Linux: the cgroup scope it runs in and the
/// menu entry the Registry needs.</summary>
public sealed class PortalIdentityTests
{
    private const string Installed = "/home/u/.local/share/RoeSnip/RoeSnip.AppImage";

    [Fact]
    public void Unit_comes_from_the_unified_line()
    {
        string cgroup = "0::/user.slice/user-1000.slice/user@1000.service/app.slice/app-com.anthropic.Claude-209750.scope\n";
        Assert.Equal("app-com.anthropic.Claude-209750.scope", DesktopIntegrationFiles.SystemdUnitOf(cgroup));
    }

    [Fact]
    public void Unit_prefers_the_unified_line_over_v1_lines()
    {
        string cgroup = "12:pids:/user.slice/session-2.scope\n0::/user.slice/user-1000.slice/user@1000.service/app.slice/app-net.roelite.roesnip-42.scope\n";
        Assert.Equal("app-net.roelite.roesnip-42.scope", DesktopIntegrationFiles.SystemdUnitOf(cgroup));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0::/\n")]
    [InlineData("0::/user.slice/user-1000.slice\n")]
    public void No_unit(string cgroup) => Assert.Null(DesktopIntegrationFiles.SystemdUnitOf(cgroup));

    [Theory]
    [InlineData("app-com.anthropic.Claude-209750.scope", "com.anthropic.Claude")]
    [InlineData("app-net.roelite.roesnip-42.scope", "net.roelite.roesnip")]
    [InlineData("app-gnome-net.roelite.roesnip-1234.scope", "net.roelite.roesnip")]
    [InlineData("app-gnome-org.gnome.Terminal-1234.scope", "org.gnome.Terminal")]
    [InlineData("app-gnome-my\\x2dapp-77.scope", "my-app")]
    [InlineData("app-org.gnome.Nautilus@a1b2.service", "org.gnome.Nautilus")]
    [InlineData("app-gnome-org.gnome.Nautilus.service", "org.gnome.Nautilus")]
    public void App_id_of_app_units(string unit, string appId) => Assert.Equal(appId, DesktopIntegrationFiles.AppIdOfUnit(unit));

    [Theory]
    [InlineData(null)]
    [InlineData("session-2.scope")]
    [InlineData("vte-spawn-1234.scope")]
    [InlineData("gnome-shell-wayland.service")]
    [InlineData("app-.scope")]
    public void No_app_id(string? unit) => Assert.Null(DesktopIntegrationFiles.AppIdOfUnit(unit));

    [Fact]
    public void Own_scope_round_trips_to_the_app_id()
    {
        string scope = DesktopIntegrationFiles.OwnScopeName(4321);
        Assert.Equal("app-net.roelite.roesnip-4321.scope", scope);
        Assert.Equal(DesktopIntegrationFiles.LinuxAppId, DesktopIntegrationFiles.AppIdOfUnit(scope));
    }

    [Fact]
    public void Installed_entry_is_never_replaced_while_the_install_exists()
    {
        string entry = DesktopIntegrationFiles.LinuxMenuEntry(Installed, "2.6.0");
        Assert.Null(DesktopIntegrationFiles.RegistrationEntryExec(entry, Installed, installExists: true, "/tmp/RoeSnip.AppImage"));
    }

    [Fact]
    public void Entry_for_an_uninstalled_AppImage_moves_to_the_launcher()
    {
        // GLib rejects a desktop file whose Exec binary is missing, so Register would fail forever.
        string entry = DesktopIntegrationFiles.LinuxMenuEntry(Installed, "2.6.0");
        Assert.Equal("/tmp/RoeSnip.AppImage", DesktopIntegrationFiles.RegistrationEntryExec(entry, Installed, installExists: false, "/tmp/RoeSnip.AppImage"));
    }

    [Fact]
    public void Non_install_entry_is_hidden_from_menus_install_entry_is_not()
    {
        Assert.True(DesktopIntegrationFiles.RegistrationEntryHidden("/tmp/RoeSnip.AppImage", Installed));
        Assert.False(DesktopIntegrationFiles.RegistrationEntryHidden(Installed, Installed));

        string hidden = DesktopIntegrationFiles.LinuxMenuEntry("/tmp/RoeSnip.AppImage", null, noDisplay: true);
        Assert.Contains("\nNoDisplay=true\n", hidden);
        Assert.True(DesktopIntegrationFiles.EntryHidden(hidden));
        string visible = DesktopIntegrationFiles.LinuxMenuEntry(Installed, "2.6.0");
        Assert.DoesNotContain("NoDisplay", visible);
        Assert.False(DesktopIntegrationFiles.EntryHidden(visible));
    }

    [Fact]
    public void Visible_entry_for_the_launcher_is_rewritten_hidden()
    {
        // Entries written before NoDisplay showed a scratch build in the app menu.
        string visible = DesktopIntegrationFiles.LinuxMenuEntry("/a/RoeSnip.AppImage", null);
        Assert.Equal("/a/RoeSnip.AppImage", DesktopIntegrationFiles.RegistrationEntryExec(visible, Installed, installExists: false, "/a/RoeSnip.AppImage"));
    }

    [Fact]
    public void Install_on_disk_wins_over_the_running_copy()
    {
        Assert.Equal(Installed, DesktopIntegrationFiles.RegistrationEntryExec(null, Installed, installExists: true, "/tmp/RoeSnip.AppImage"));
        string portable = DesktopIntegrationFiles.LinuxMenuEntry("/tmp/RoeSnip.AppImage", null);
        Assert.Equal(Installed, DesktopIntegrationFiles.RegistrationEntryExec(portable, Installed, installExists: true, "/tmp/RoeSnip.AppImage"));
    }

    [Fact]
    public void Not_installed_points_at_the_launcher_and_follows_it()
    {
        Assert.Equal("/a/RoeSnip.AppImage", DesktopIntegrationFiles.RegistrationEntryExec(null, Installed, installExists: false, "/a/RoeSnip.AppImage"));
        string current = DesktopIntegrationFiles.LinuxMenuEntry("/a/RoeSnip.AppImage", null, noDisplay: true);
        Assert.Null(DesktopIntegrationFiles.RegistrationEntryExec(current, Installed, installExists: false, "/a/RoeSnip.AppImage"));
        Assert.Equal("/b/RoeSnip.AppImage", DesktopIntegrationFiles.RegistrationEntryExec(current, Installed, installExists: false, "/b/RoeSnip.AppImage"));
    }

    [Fact]
    public void No_launcher_leaves_things_alone()
    {
        Assert.Null(DesktopIntegrationFiles.RegistrationEntryExec(null, Installed, installExists: false, null));
    }

    [Fact]
    public void Entry_launches_matches_main_and_action_exec_only_exactly()
    {
        string entry = DesktopIntegrationFiles.LinuxMenuEntry("/a b/RoeSnip.AppImage", null);
        Assert.True(DesktopIntegrationFiles.EntryLaunches(entry, "/a b/RoeSnip.AppImage"));
        Assert.False(DesktopIntegrationFiles.EntryLaunches(entry, "/a b/RoeSnip"));
    }
}
