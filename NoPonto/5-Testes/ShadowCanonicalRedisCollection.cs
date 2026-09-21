using Xunit;

namespace NoPonto.Tests;

// These integration tests use the same canonical Redis Stream and DLQ keys.
[CollectionDefinition("Shadow canonical Redis keys", DisableParallelization = true)]
public sealed class ShadowCanonicalRedisCollection { }
