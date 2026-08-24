using Janus.Server;
using Janus.Storage;
using Xunit;

namespace Janus.Tests;

public sealed class VerticalSliceTests
{
    [Fact]
    public async Task AssetAndFactPersistAcrossRepositoryRestart()
    {
        var fixture = new DatabaseFixture();
        try
        {
            var firstInstance = new SqliteJanusRepository(fixture.DatabasePath);
            var created = await JanusTools.CreateAssetAsync(
                firstInstance,
                "John Deere 8Y Cart",
                type: "cart",
                manufacturer: "John Deere",
                model: "8Y",
                aliases: ["John Deere cart"]);

            Assert.Equal("ok", created.Status);

            var saved = await JanusTools.SetFactAsync(
                firstInstance,
                "John Deere 8Y Cart",
                "tire_pressure",
                "30",
                unit: "psi",
                valueType: "number",
                source: "user");

            Assert.Equal("ok", saved.Status);

            // A new repository instance models a Janus process restart.
            var restartedInstance = new SqliteJanusRepository(fixture.DatabasePath);
            var search = await JanusTools.SearchAsync(
                restartedInstance,
                "John Deere cart tire pressure");

            var result = Assert.Single(search.Results);
            Assert.Equal("John Deere 8Y Cart", result.Asset.Name);
            var fact = Assert.Single(result.Facts);
            Assert.Equal("tire_pressure", fact.Key);
            Assert.Equal("30", fact.Value);
            Assert.Equal("psi", fact.Unit);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task AmbiguousHumanReferenceReturnsCandidates()
    {
        var fixture = new DatabaseFixture();
        try
        {
            var repository = new SqliteJanusRepository(fixture.DatabasePath);
            await JanusTools.CreateAssetAsync(repository, "North Workshop Cart", aliases: ["shop cart"]);
            await JanusTools.CreateAssetAsync(repository, "South Workshop Cart", aliases: ["shop cart"]);

            var response = await JanusTools.SetFactAsync(
                repository,
                "shop cart",
                "tire_pressure",
                "30",
                unit: "psi");

            Assert.Equal("ambiguous", response.Status);
            Assert.Equal(2, response.Candidates.Count);
            Assert.Null(response.Fact);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task UpdateEventAndFactRemovalUseHumanAlias()
    {
        var fixture = new DatabaseFixture();
        try
        {
            var repository = new SqliteJanusRepository(fixture.DatabasePath);
            await JanusTools.CreateAssetAsync(repository, "John Deere 8Y Cart", aliases: ["yard cart"]);
            await JanusTools.SetFactAsync(repository, "yard cart", "tire_pressure", "30", unit: "psi");

            var updated = await JanusTools.UpdateAssetAsync(
                repository,
                "yard cart",
                description: "Utility dump cart");
            var recorded = await JanusTools.RecordEventAsync(
                repository,
                "yard cart",
                "tire inflated",
                "Inflated both tires to specification.");
            var removed = await JanusTools.RemoveFactAsync(repository, "yard cart", "tire_pressure");
            var fetched = await JanusTools.GetAssetAsync(repository, "yard cart");

            Assert.Equal("Utility dump cart", updated.Result?.Asset.Description);
            Assert.Equal("ok", recorded.Status);
            Assert.Equal("ok", removed.Status);
            Assert.Empty(fetched.Result?.Facts ?? throw new InvalidOperationException("Expected an asset result."));
            Assert.Single(fetched.Result.Events);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"janus-tests-{Guid.NewGuid():N}");

        public string DatabasePath => Path.Combine(_directory, "janus.db");

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
