namespace Arcanist.YouTube.Tests;

public sealed class UrlClassifierTests
{
    private static readonly string[] NoSites = [];

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", LinkKind.Video, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc&t=42", LinkKind.Video, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("youtu.be/dQw4w9WgXcQ", LinkKind.Video, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ&feature=share", LinkKind.Video, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", LinkKind.Video, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ?si=x", LinkKind.Video, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", LinkKind.Video, "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ", LinkKind.Video, "https://music.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/playlist?list=PL1234567890abcdef", LinkKind.Playlist, "https://www.youtube.com/playlist?list=PL1234567890abcdef", "PL1234567890abcdef")]
    [InlineData("https://music.youtube.com/playlist?list=OLAK5uy_abcdefghijk", LinkKind.Album, "https://music.youtube.com/playlist?list=OLAK5uy_abcdefghijk", "OLAK5uy_abcdefghijk")]
    [InlineData("https://music.youtube.com/browse/MPREb_abcdefghijk", LinkKind.Album, "https://music.youtube.com/browse/MPREb_abcdefghijk", "MPREb_abcdefghijk")]
    [InlineData("https://www.youtube.com/playlist?list=RDdQw4w9WgXcQ", LinkKind.Mix, "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDdQw4w9WgXcQ", "RDdQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/@SomeBand", LinkKind.Channel, "https://www.youtube.com/@SomeBand", "@SomeBand")]
    [InlineData("https://www.youtube.com/@SomeBand/releases", LinkKind.Channel, "https://www.youtube.com/@SomeBand", "@SomeBand")]
    [InlineData("https://www.youtube.com/channel/UC1234567890abcdefghij/videos", LinkKind.Channel, "https://www.youtube.com/channel/UC1234567890abcdefghij", "UC1234567890abcdefghij")]
    [InlineData("https://www.youtube.com/c/SomeBand", LinkKind.Channel, "https://www.youtube.com/c/SomeBand", "SomeBand")]
    [InlineData("https://www.youtube.com/user/somebandvevo", LinkKind.Channel, "https://www.youtube.com/user/somebandvevo", "somebandvevo")]
    [InlineData("https://music.youtube.com/channel/UC1234567890abcdefghij", LinkKind.Artist, "https://music.youtube.com/channel/UC1234567890abcdefghij", "UC1234567890abcdefghij")]
    public void Every_form_of_YouTube_link_is_understood(string text, LinkKind kind, string url, string id)
    {
        var link = UrlClassifier.Classify(text, NoSites, out _);
        Assert.NotNull(link);
        Assert.Equal((kind, url, id), (link.Kind, link.Url, link.Id));
    }

    [Fact]
    public void A_video_inside_a_list_offers_the_list()
    {
        var link = UrlClassifier.Classify("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL1234567890abcdef&index=3", NoSites, out _)!;
        Assert.Equal(LinkKind.Video, link.Kind);
        Assert.Equal("https://www.youtube.com/playlist?list=PL1234567890abcdef", link.ListUrl);

        // A mix opens from its video.
        var mix = UrlClassifier.Classify("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDdQw4w9WgXcQ&start_radio=1", NoSites, out _)!;
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDdQw4w9WgXcQ", mix.ListUrl);
    }

    [Fact]
    public void Channel_tabs_are_kept()
    {
        var link = UrlClassifier.Classify("https://www.youtube.com/@SomeBand/videos?view=0", NoSites, out _)!;
        Assert.Equal("videos", link.Tab);
        Assert.Equal("https://www.youtube.com/@SomeBand/releases", UrlClassifier.ChannelTab(link, "releases"));
    }

    [Fact]
    public void Links_are_found_in_pasted_text()
    {
        var links = UrlClassifier.FindLinks("Mira esto: https://youtu.be/dQw4w9WgXcQ?si=x, y también\nwww.youtube.com/watch?v=aaaaaaaaaaa).\nhttps://youtu.be/dQw4w9WgXcQ?si=x");
        Assert.Equal(["https://youtu.be/dQw4w9WgXcQ?si=x", "https://www.youtube.com/watch?v=aaaaaaaaaaa"], links);
        Assert.Equal("dQw4w9WgXcQ", UrlClassifier.Classify("Escucha (https://youtu.be/dQw4w9WgXcQ)", NoSites, out _)!.Id);
    }

    [Theory]
    [InlineData("not a link")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://www.youtube.com/")]
    [InlineData("ftp://youtube.com/watch?v=dQw4w9WgXcQ")]
    public void What_is_not_a_link_says_so(string text)
    {
        Assert.Null(UrlClassifier.Classify(text, NoSites, out var problem));
        Assert.Equal(LinkProblem.Invalid, problem);
    }

    [Fact]
    public void Other_sites_need_the_administrator()
    {
        Assert.Null(UrlClassifier.Classify("https://soundcloud.com/artist/song", NoSites, out var problem));
        Assert.Equal(LinkProblem.NotAllowed, problem);

        var allowed = UrlClassifier.Classify("https://m.soundcloud.com/artist/song?utm=1", ["soundcloud.com"], out _);
        Assert.Equal(LinkKind.Other, allowed!.Kind);
        Assert.Null(UrlClassifier.Classify("https://notsoundcloud.com/x", ["soundcloud.com"], out _));
    }

    [Theory]
    [InlineData("Band Name - Song Title (Official Video)", "Song Title", "Band Name")]
    [InlineData("Band – Song [Official Audio] | Label", "Song", "Band")]
    [InlineData("Artista - Canción (Video Oficial)", "Canción", "Artista")]
    [InlineData("Just a title", "Just a title", "The Channel")]
    public void Tags_are_guessed_from_titles(string title, string expectedTitle, string expectedArtist)
    {
        var tags = TagGuesser.Guess(new System.Text.Json.Nodes.JsonObject { ["title"] = title, ["channel"] = "The Channel - Topic", ["upload_date"] = "20190105" });
        Assert.Equal((expectedTitle, expectedArtist, 2019), (tags.Title, tags.Artist, tags.Year));
    }

    [Fact]
    public void YouTube_Music_knows_the_song()
    {
        var tags = TagGuesser.Guess(new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = "Whatever", ["track"] = "Real Title", ["artists"] = new System.Text.Json.Nodes.JsonArray("One", "Two"), ["album"] = "The Album",
            ["release_year"] = 2001,
        });
        Assert.Equal(("Real Title", "One; Two", "The Album", 2001), (tags.Title, tags.Artist, tags.Album, tags.Year));
    }
}
