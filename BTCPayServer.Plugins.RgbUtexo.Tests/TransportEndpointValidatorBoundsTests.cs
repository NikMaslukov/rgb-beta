using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// DisableParallelization is load-bearing twice over. xunit 2.9.3 honours Fact.Timeout only when
// parallelization is disabled globally or for the containing collection, and the per-endpoint-cap
// case depends on that timeout to fail fast instead of hanging CI for the stub's full delay.
// It also serializes the two process-wide statics these tests manipulate: the resolver seam, and
// TaskScheduler.UnobservedTaskException.
[CollectionDefinition(TransportEndpointValidatorCollection.Name, DisableParallelization = true)]
public sealed class TransportEndpointValidatorCollection
{
    public const string Name = "TransportEndpointValidator";
}

[Collection(TransportEndpointValidatorCollection.Name)]
public class TransportEndpointValidatorBoundsTests
{
}
