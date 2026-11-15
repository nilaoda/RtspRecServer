using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RtspRecServer.Server.Services.Epg;
using RtspRecServer.Shared;

namespace RtspRecServer.Tests.Epg;

public class XmlTvParserTests
{
    [Fact]
    public async Task Parse_MapsChannelsProgramsAndCategories()
    {
        var data = await ParseAsync("""
            <tv>
              <channel id="c1">
                <display-name lang="en">CCTV-1</display-name>
                <display-name lang="zh">CCTV-1 综合</display-name>
                <icon src="http://logo/c1.png" />
              </channel>
              <channel id="c2">
                <display-name>CCTV-2</display-name>
              </channel>
              <programme channel="c1" start="20260102120000 +0800" stop="20260102130000 +0800">
                <title lang="en">News</title>
                <title lang="zh">新闻联播</title>
                <desc>晚间新闻</desc>
                <category>新闻</category>
                <category>综合</category>
              </programme>
              <programme channel="c2" start="20260102120000 +0800" stop="20260102123000 +0800">
                <title>电视剧</title>
              </programme>
            </tv>
            """);

        Assert.Equal(2, data.Channels.Count);

        var channel = Assert.Single(data.Channels, item => item.Id == "c1");
        Assert.Equal("CCTV-1 综合", channel.Name);
        Assert.Equal("http://logo/c1.png", channel.IconUrl);
        Assert.Contains("新闻", channel.Categories);
        Assert.Contains("综合", channel.Categories);

        var program = Assert.Single(data.ChannelPrograms["c1"]);
        Assert.Equal("新闻联播", program.Title);
        Assert.Equal("晚间新闻", program.Description);
        Assert.Equal("新闻", program.Category);
        Assert.Equal(2, program.Categories.Count);
        Assert.Equal(ExpectedLocal("2026-01-02T12:00:00+08:00"), program.StartTime);
        Assert.Equal(ExpectedLocal("2026-01-02T13:00:00+08:00"), program.EndTime);
    }

    [Fact]
    public async Task Parse_PrefersChineseDisplayNameAndTitle()
    {
        var data = await ParseAsync("""
            <tv>
              <channel id="c1">
                <display-name lang="en">English Name</display-name>
                <display-name lang="zh">中文名称</display-name>
              </channel>
              <programme channel="c1" start="20260102120000 +0800" stop="20260102130000 +0800">
                <title lang="en">English Title</title>
                <title lang="zh">中文标题</title>
              </programme>
            </tv>
            """);

        Assert.Equal("中文名称", Assert.Single(data.Channels).Name);
        Assert.Equal("中文标题", Assert.Single(data.ChannelPrograms["c1"]).Title);
    }

    [Fact]
    public async Task Parse_SupportsXmlTvTimeFormatsAndOffsets()
    {
        var data = await ParseAsync("""
            <tv>
              <channel id="c1"><display-name>C1</display-name></channel>
              <programme channel="c1" start="20260102120000 +0800" stop="20260102130000 +0800">
                <title>seconds</title>
              </programme>
              <programme channel="c1" start="202601021200 +0800" stop="202601021300 +0800">
                <title>minutes</title>
              </programme>
              <programme channel="c1" start="20260102 +0800" stop="20260103 +0800">
                <title>days</title>
              </programme>
              <programme channel="c1" start="20260102120000 -0500" stop="20260102130000 -0500">
                <title>negative</title>
              </programme>
              <programme channel="c1" start="20260102120000" stop="20260102130000">
                <title>local</title>
              </programme>
            </tv>
            """);

        Assert.Equal(ExpectedLocal("2026-01-02T12:00:00+08:00"), FindProgram(data, "seconds").StartTime);
        Assert.Equal(ExpectedLocal("2026-01-02T12:00:00+08:00"), FindProgram(data, "minutes").StartTime);
        Assert.Equal(ExpectedLocal("2026-01-02T00:00:00+08:00"), FindProgram(data, "days").StartTime);
        Assert.Equal(ExpectedLocal("2026-01-02T12:00:00-05:00"), FindProgram(data, "negative").StartTime);
        Assert.Equal(new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Local), FindProgram(data, "local").StartTime);
    }

    [Fact]
    public async Task Parse_SortsProgramsAndBuildsIds()
    {
        var data = await ParseAsync("""
            <tv>
              <channel id="c1"><display-name>C1</display-name></channel>
              <programme channel="c1" start="20260102120000 +0800" stop="20260102130000 +0800">
                <title>later</title>
              </programme>
              <programme channel="c1" start="20260102100000 +0800" stop="20260102110000 +0800">
                <title>earlier</title>
              </programme>
            </tv>
            """);

        var programs = data.ChannelPrograms["c1"];
        Assert.Collection(
            programs,
            first => Assert.Equal("earlier", first.Title),
            second => Assert.Equal("later", second.Title));

        foreach (var program in programs)
        {
            Assert.Equal($"c1_{program.StartTime:yyyyMMddHHmmss}", program.Id);
        }
    }

    [Fact]
    public async Task Parse_SkipsProgramsWithMissingRequiredFields()
    {
        var data = await ParseAsync("""
            <tv>
              <channel id="c1"><display-name>C1</display-name></channel>
              <programme channel="c1" start="bad" stop="20260102130000 +0800">
                <title>bad time</title>
              </programme>
              <programme channel="c1" start="20260102120000 +0800">
                <title>missing stop</title>
              </programme>
              <programme channel="c1" start="20260102120000 +0800" stop="20260102130000 +0800"></programme>
              <programme start="20260102120000 +0800" stop="20260102130000 +0800">
                <title>missing channel</title>
              </programme>
              <programme channel="c1" start="20260102120000 +0800" stop="20260102130000 +0800">
                <title>valid</title>
              </programme>
            </tv>
            """);

        Assert.Equal("valid", Assert.Single(data.ChannelPrograms["c1"]).Title);
    }

    [Fact]
    public async Task Parse_UsesChannelIdAsNameForEmptyChannelElement()
    {
        var data = await ParseAsync("""
            <tv>
              <channel id="only-id" />
            </tv>
            """);

        Assert.Equal("only-id", Assert.Single(data.Channels).Name);
    }

    [Fact]
    public async Task Parse_ReadsOptionalProgramMarkers()
    {
        var data = await ParseAsync("""
            <tv>
              <channel id="c1"><display-name>C1</display-name></channel>
              <programme channel="c1" start="20260102120000 +0800" stop="20260102130000 +0800">
                <title>标记</title>
                <sub-title>副标题</sub-title>
                <episode-num system="xmltv_ns">12</episode-num>
                <star-rating><value>5</value></star-rating>
                <rating system="MPAA" value="PG"></rating>
                <premiere />
                <new />
                <previously-shown />
                <subtitles />
                <video><quality>HD</quality></video>
              </programme>
            </tv>
            """);

        var program = Assert.Single(data.ChannelPrograms["c1"]);
        Assert.Equal("副标题", program.SubTitle);
        Assert.Equal(12, program.EpisodeNumber);
        Assert.Equal("5", program.StarRating);
        Assert.Equal("MPAA:PG", program.Rating);
        Assert.True(program.IsPremiere);
        Assert.True(program.IsNew);
        Assert.True(program.IsRepeat);
        Assert.True(program.IsSubtitled);
        Assert.True(program.IsHd);
    }

    private static async Task<EpgDataCache> ParseAsync(string xml)
    {
        var parser = new XmlTvParserService(NullLogger<XmlTvParserService>.Instance);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return await parser.ParseXmlTvAsync(stream);
    }

    private static EpgProgram FindProgram(EpgDataCache data, string title)
    {
        return data.ChannelPrograms.Values
            .SelectMany(programs => programs)
            .Single(program => program.Title == title);
    }

    private static DateTime ExpectedLocal(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).LocalDateTime;
    }
}
