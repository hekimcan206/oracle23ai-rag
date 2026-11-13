using OracleRag.Answering;
using OracleRag.Retrieval;

namespace OracleRag.UnitTests.Answering;

public sealed class ExtractiveAnswererTests
{
    private static SearchHit Hit(string entity, string heading, string text, double distance = 0.3, string topic = "Visiting Hours") =>
        new($"{entity}#0000", entity, 0, heading, text, $"{entity} — {topic}", distance);

    private readonly ExtractiveAnswerer _answerer = new();

    [Fact]
    public void Answers_with_a_verbatim_sentence_and_cites_its_chunk()
    {
        var answer = _answerer.Answer(
            "How many minutes can a visitor stay in intensive care at Kuzeykent Clinic?",
            [Hit("Kuzeykent Clinic", "Intensive care", "Intensive care visits are limited to 20 minutes and one visitor at a time. Visitors must wear a gown.")]);

        answer.Abstained.Should().BeFalse();
        answer.Text.Should().Be("Intensive care visits are limited to 20 minutes and one visitor at a time. [1]");
        answer.Citations.Should().ContainSingle().Which.ChunkId.Should().Be("Kuzeykent Clinic#0000");
    }

    [Fact]
    public void A_what_time_question_prefers_the_sentence_with_a_clock_time()
    {
        var answer = _answerer.Answer(
            "At what time do weekday visiting hours end at Kuzeykent Clinic?",
            [Hit("Kuzeykent Clinic", "Weekday visits", "Visitors are welcome between 10:00 and 20:00 on weekdays. Each patient may receive at most 3 visitors at the same time.")]);

        answer.Text.Should().StartWith("Visitors are welcome between 10:00 and 20:00");
    }

    [Fact]
    public void Right_topic_for_the_wrong_entity_is_refused()
    {
        var answer = _answerer.Answer(
            "How much does the car park at Kuzeykent Clinic charge per hour?",
            [Hit("Güneyova Clinic", "Car park", "The car park has 200 spaces and charges 40 TL per hour.", 0.35, "Parking")]);

        answer.Abstained.Should().BeTrue();
        answer.Text.Should().Be(ExtractiveAnswerer.AbstentionText);
        answer.Citations.Should().BeEmpty();
    }

    [Fact]
    public void Right_entity_for_a_topic_it_does_not_cover_is_refused()
    {
        var answer = _answerer.Answer(
            "How much does the car park at Kuzeykent Clinic charge per hour?",
            [Hit("Kuzeykent Clinic", "Children", "Children under 12 may visit only together with an adult.")]);

        answer.Abstained.Should().BeTrue();
    }

    [Fact]
    public void Nothing_close_enough_means_abstention()
    {
        _answerer.Answer("Where is Kuzeykent Clinic?", [Hit("Kuzeykent Clinic", "Address", "Kuzeykent Clinic is on Main Street.", 0.9)]).Abstained.Should().BeTrue();
        _answerer.Answer("Where is Kuzeykent Clinic?", []).Abstained.Should().BeTrue();
        _answerer.Answer("the of and", [Hit("Kuzeykent Clinic", "Address", "x")]).Abstained.Should().BeTrue();
    }

    [Theory]
    [InlineData("How much does the car park at Kuzeykent Sahil Clinic charge?", "kuzeykent sahil clinic")]
    [InlineData("how much does parking cost", "")]
    public void Name_tokens_are_the_capitalised_words_after_the_first(string question, string expected) =>
        string.Join(' ', ExtractiveAnswerer.NameTokens(question).Order()).Should().Be(string.Join(' ', expected.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order()));

    [Theory]
    [InlineData("Until what time is the pharmacy open?", "Time")]
    [InlineData("How many days?", "Number")]
    [InlineData("Under what age must children be accompanied?", "Number")]
    [InlineData("Next to which entrance are the spaces?", "Any")]
    public void Expected_answer_type_is_detected(string question, string expected) =>
        ExtractiveAnswerer.ExpectedAnswerType(question).ToString().Should().Be(expected);
}
