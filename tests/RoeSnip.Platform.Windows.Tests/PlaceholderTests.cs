using Xunit;

namespace RoeSnip.Platform.Windows.Tests;

/// <summary>WP-X1 stub so this project is in RoeSnip.sln from day one (later packages must never
/// touch the .sln) and `dotnet test` on the solution has at least one discoverable test here.
/// WP-X2 owns this project (PLAN-XPLAT.md §3.2) and should DELETE this file when it adds the real
/// JxrRoundTripTests.cs.</summary>
public class PlaceholderTests
{
    [Fact]
    public void ProjectStub_Compiles()
    {
        Assert.True(true);
    }
}
