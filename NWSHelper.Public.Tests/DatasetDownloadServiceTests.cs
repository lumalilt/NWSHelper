using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NWSHelper.Gui.Services;
using Xunit;

namespace NWSHelper.Tests;

public class DatasetDownloadServiceTests
{
    [Theory]
    [InlineData("\"2026-10-16T17:07:35.668Z\"", "\"2026-10-16T18:07:35.668+01:00\"", 20)]
    [InlineData("\"2026-10-16T17:07:35.669Z\"", "\"2026-10-16T17:07:35.668Z\"", 10)]
    [InlineData("1720000000", "1720000001", 20)]
    [InlineData("\"2024-07-03T09:46:40.500Z\"", "1720000000", 10)]
    [InlineData("null", "\"2026-10-16T17:07:35.668Z\"", 20)]
    [InlineData("\"bad timestamp\"", "1720000000", 20)]
    [InlineData("{}", "null", 20)]
    [InlineData("true", "[]", 20)]
    public async Task GetDatasetsAsync_AcceptsCatalogTimestampFormatsAndOrdersNewestFirst(
        string firstUpdated, string secondUpdated, int expectedJob)
    {
        var payload = $$"""
            [
              {"source":"us/md/test","job":10,"updated":{{firstUpdated}},"size":100},
              {"source":"us/md/test","job":20,"updated":{{secondUpdated}},"size":200},
              {"source":"us/va/test","job":30}
            ]
            """;
        using var client = new HttpClient(new CatalogHandler(payload));
        using var service = new DatasetDownloadService(client);

        var datasets = await service.GetDatasetsAsync("openaddresses", "https://synthetic.invalid/api", CancellationToken.None);

        Assert.Equal(2, datasets.Count);
        Assert.Equal("us/md/test", datasets[0].Key);
        Assert.Equal(expectedJob, datasets[0].JobId);
        Assert.Equal(expectedJob * 10, datasets[0].SizeBytes);
        Assert.Equal(30, datasets[1].JobId);
    }

    private sealed class CatalogHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/data?layer=addresses", request.RequestUri!.PathAndQuery);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task DownloadDatasetsAsync_ParsesJsonLinesWithoutFailing()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "nwshelper-dataset-download-jsonl", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            using var service = new DatasetDownloadService(CreateHttpClient(CreateJsonLinesPayload()));

            var result = await service.DownloadDatasetsAsync(
                new DatasetDownloadRequest(
                    "openaddresses",
                    tempDirectory,
                    ["us/md/harford"],
                    "https://batch.openaddresses.io/api",
                    null),
                progress: null,
                CancellationToken.None);

            var outputPath = Path.Combine(tempDirectory, "openaddresses", "us", "md", "harford.csv");
            var csv = await File.ReadAllTextAsync(outputPath);

            Assert.Equal(1, result.DownloadedCount);
            Assert.Contains("number,house_number,street,unit,city,region,postcode,lat,lon,name,phone,type", csv, StringComparison.Ordinal);
            Assert.Contains("123,123,Main St", csv, StringComparison.Ordinal);
            Assert.Contains("124,124,Oak Ave", csv, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadDatasetsAsync_ParsesFeatureCollectionDocument()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "nwshelper-dataset-download-feature-collection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            using var service = new DatasetDownloadService(CreateHttpClient(CreateFeatureCollectionPayload()));

            var result = await service.DownloadDatasetsAsync(
                new DatasetDownloadRequest(
                    "openaddresses",
                    tempDirectory,
                    ["us/md/harford"],
                    "https://batch.openaddresses.io/api",
                    null),
                progress: null,
                CancellationToken.None);

            var outputPath = Path.Combine(tempDirectory, "openaddresses", "us", "md", "harford.csv");
            var csv = await File.ReadAllTextAsync(outputPath);

            Assert.Equal(1, result.DownloadedCount);
            Assert.Contains("321,321,Pine Rd", csv, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static HttpClient CreateHttpClient(byte[] gzippedPayload)
    {
        return new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri is null)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/data", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[{\"source\":\"us/md/harford\",\"job\":42,\"size\":1,\"updated\":1}]", Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/job/42/output/source.geojson.gz", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(gzippedPayload)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
    }

    private static byte[] CreateJsonLinesPayload()
    {
        const string payload = """
{"type":"Feature","properties":{"number":"123","street":"Main St","city":"Bel Air","region":"MD","postcode":"21014","lat":"39.535","lon":"-76.348"},"geometry":{"type":"Point","coordinates":[-76.348,39.535]}}
{"type":"Feature","properties":{"number":"124","street":"Oak Ave","city":"Bel Air","region":"MD","postcode":"21014","lat":"39.536","lon":"-76.349"},"geometry":{"type":"Point","coordinates":[-76.349,39.536]}}
""";

        return Gzip(payload);
    }

    private static byte[] CreateFeatureCollectionPayload()
    {
        const string payload = """
{"type":"FeatureCollection","features":[{"type":"Feature","properties":{"number":"321","street":"Pine Rd","city":"Bel Air","region":"MD","postcode":"21014","lat":"39.537","lon":"-76.350"},"geometry":{"type":"Point","coordinates":[-76.350,39.537]}}]}
""";

        return Gzip(payload);
    }

    private static byte[] Gzip(string payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(payload);
        }

        return output.ToArray();
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
