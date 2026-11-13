using FsCheck;
using FsCheck.Xunit;
using OracleRag.Text;

namespace OracleRag.UnitTests.Text;

public sealed class TextNormalizerTests
{
    [Theory]
    [InlineData("İzmir", "izmir")]
    [InlineData("IZMIR", "izmir")]
    [InlineData("Işıklıova", "isikliova")]
    [InlineData("ŞİŞLİ", "sisli")]
    [InlineData("Gülyaka Çarşı", "gulyaka carsi")]
    [InlineData("Nikâh", "nikah")]
    public void Turkish_letters_fold_to_ascii_regardless_of_case(string input, string expected) =>
        string.Join(' ', TextNormalizer.Tokens(input)).Should().Be(expected);

    [Theory]
    [InlineData("2.450 TL", "2450 tl")]
    [InlineData("2,450 TL", "2450 tl")]
    [InlineData("open 9:00 to 21:00", "open 9 00 to 21 00")]
    [InlineData("v1.2 end. Next", "v12 end next")]
    public void Thousands_separators_between_digits_do_not_split_numbers(string input, string expected) =>
        string.Join(' ', TextNormalizer.Tokens(input)).Should().Be(expected);

    [Fact]
    public void Content_tokens_drop_stop_words_and_fold_plurals()
    {
        TextNormalizer.ContentTokens("How many visitors are allowed in the rooms?")
            .Should().Equal("many", "visitor", "allowed", "room");
    }

    [Theory]
    [InlineData("hours", "hour")]
    [InlineData("class", "class")]
    [InlineData("bus", "bus")]
    [InlineData("1990s", "1990s")]
    public void Plural_fold_is_conservative(string token, string expected) =>
        TextNormalizer.FoldPlural(token).Should().Be(expected);

    [Property]
    public bool Tokens_are_lower_case_ascii_or_digits_for_turkish_input(NonNull<string> text)
    {
        var turkish = text.Get + " İŞĞÜÖÇışğüöç";
        return TextNormalizer.Tokens(turkish).SelectMany(t => t).All(c => !"İŞĞÜÖÇışğüöç".Contains(c) && !char.IsUpper(c));
    }

    [Property]
    public bool Tokenisation_is_idempotent_on_its_own_output(NonNull<string> text)
    {
        var once = TextNormalizer.Tokens(text.Get);
        return TextNormalizer.Tokens(string.Join(' ', once)).SequenceEqual(once);
    }
}
