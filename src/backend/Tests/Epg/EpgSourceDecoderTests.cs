using System.IO.Compression;
using System.Text;
using RtspRecServer.Server.Services.Epg;

namespace RtspRecServer.Tests.Epg;

public class EpgSourceDecoderTests
{
    private const string Xml = "<?xml version=\"1.0\"?><tv><channel id=\"c1\"/></tv>";

    [Fact]
    public async Task GzipPayload_IsDecodedToXml()
    {
        var payload = Gzip(Xml);

        var decoded = await Decode(payload);

        Assert.Equal("gzip", decoded.Format);
        Assert.Equal(Xml, await ReadAll(decoded.Stream));
    }

    [Fact]
    public async Task PlainXmlPayload_IsPassedThrough()
    {
        // 部分源（如 51zmt e.xml.gz）以 .gz 地址返回未压缩 XML
        var payload = Encoding.UTF8.GetBytes(Xml);

        var decoded = await Decode(payload);

        Assert.Equal("plain", decoded.Format);
        Assert.Equal(Xml, await ReadAll(decoded.Stream));
    }

    [Fact]
    public async Task ZipWithXmlEntry_IsDecoded()
    {
        var payload = CreateZip(("e.xml", Encoding.UTF8.GetBytes(Xml)));

        var decoded = await Decode(payload);

        Assert.Equal("zip(xml)", decoded.Format);
        Assert.Equal(Xml, await ReadAll(decoded.Stream));
    }

    [Fact]
    public async Task ZipWithNestedGzEntry_IsDecoded()
    {
        var payload = CreateZip(("e.xml.gz", Gzip(Xml)));

        var decoded = await Decode(payload);

        Assert.Equal("zip(xml.gz)", decoded.Format);
        Assert.Equal(Xml, await ReadAll(decoded.Stream));
    }

    [Fact]
    public async Task ZipWithoutXmlEntry_ThrowsWithClearMessage()
    {
        var payload = CreateZip(("readme.txt", "no epg here"u8.ToArray()));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Decode(payload));

        Assert.Contains("没有找到", ex.Message);
    }

    [Fact]
    public async Task ShortStreamWithSingleByte_IsTreatedAsPlain()
    {
        var decoded = await Decode("x"u8.ToArray());

        Assert.Equal("plain", decoded.Format);
        Assert.Equal("x", await ReadAll(decoded.Stream));
    }

    private static async Task<DecodedEpgSource> Decode(byte[] payload)
    {
        var source = new MemoryStream(payload);
        var decoded = await EpgSourceDecoder.DecodeToXmlStreamAsync(source);
        return decoded with { Stream = new MemoryStream(Encoding.UTF8.GetBytes(await ReadAll(decoded.Stream))) };
    }

    private static async Task<string> ReadAll(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static byte[] Gzip(string content)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, Encoding.UTF8))
        {
            writer.Write(content);
        }
        return compressed.ToArray();
    }

    private static byte[] CreateZip(params (string Name, byte[] Content)[] entries)
    {
        using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }
        return archiveStream.ToArray();
    }
}
