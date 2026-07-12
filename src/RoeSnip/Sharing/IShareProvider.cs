using System.Threading;
using System.Threading.Tasks;

namespace RoeSnip.Sharing;

/// <summary>The one abstraction every share target implements. In practice there is exactly one
/// concrete implementation (<see cref="ProviderSpecShareProvider"/>) that executes a declarative
/// <see cref="ProviderSpec"/> - the interface exists so ShareManager/tests can depend on "something
/// that uploads a file and returns a URL" without caring whether it's driven by a built-in spec, a
/// custom one, or (in tests) a stub.</summary>
public interface IShareProvider
{
    string DisplayName { get; }

    /// <summary>Never throws for an ordinary failure (network error, non-2xx response, unparseable
    /// response) - those all come back as a Success=false result with ErrorMessage set. May throw
    /// OperationCanceledException if cancellationToken is signalled (callers should cancel an
    /// in-flight upload on teardown - see ShareManager).</summary>
    Task<ShareUploadResult> UploadAsync(ShareUploadRequest request, CancellationToken cancellationToken);
}
