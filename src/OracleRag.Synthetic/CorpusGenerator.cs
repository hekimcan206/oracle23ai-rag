using System.Globalization;
using System.Text;
using OracleRag.Chunking;
using OracleRag.Text;

namespace OracleRag.Synthetic;

/// <summary>A question with its gold answer: the chunk that answers it and a substring the answer must contain.</summary>
/// <param name="ExpectedChunkId">Null for unanswerable questions (the topic is not covered for that entity).</param>
public sealed record QaItem(string Id, string Question, string? ExpectedChunkId, string? ExpectedAnswer)
{
    public bool Answerable => ExpectedChunkId is not null;
}

public sealed record GeneratedCorpus(IReadOnlyList<SourceDocument> Documents, IReadOnlyList<QaItem> Questions);

/// <summary>
/// Seeded generator for a synthetic patient-information corpus: fictional clinics in fictional Turkish towns, each
/// publishing the same set of guides (visiting hours, parking, lab results, ...) with entity-specific numbers.
/// </summary>
/// <remarks>
/// The corpus is built from <b>near-duplicate families</b> on purpose. Every clinic's parking guide reads almost the
/// same; only the entity name and the numbers differ. That is what real institutional document sets look like, and it
/// is the hard case for retrieval: finding the right topic is easy, finding the right clinic is not. Each entity omits
/// one topic, and the question set asks about it, so abstention is tested too. All names are invented.
/// </remarks>
public static class CorpusGenerator
{
    private static readonly string[] TownPrefixes =
    [
        "Ak", "Kara", "Yeşil", "Kuzey", "Güney", "Çam", "Işık", "Mavi", "Gül", "Taş", "Demir", "Altın", "Bahar", "Deniz",
        "Gök", "Kır", "Ilgın", "Pınar", "Sarı", "Yıldız",
    ];

    private static readonly string[] TownSuffixes =
    [
        "kent", "ova", "yaka", "pınar", "dere", "tepe", "köy", "alan", "bağ", "burç", "yurt", "liman", "vadi", "kaya", "göl",
    ];

    private static readonly string[] Kinds = ["Clinic", "Hospital", "Health Centre", "Dental Clinic", "Eye Clinic", "Physiotherapy Centre"];

    private static readonly string[] Branches = ["", "Merkez", "Sahil", "İstasyon", "Çarşı", "Lise"];

    private static readonly string[] Gates = ["north", "south", "east", "west", "main", "emergency"];

    /// <summary>Topics in a fixed order; <see cref="Topic.Sections"/> become <c>##</c> sections and hence chunks.</summary>
    internal static readonly Topic[] Topics =
    [
        new("visiting-hours", "Visiting Hours",
        [
            new("Weekday visits", f => $"Visitors are welcome between {f.Hour(9, 12)}:00 and {f.Hour(18, 21)}:00 on weekdays. Each patient may receive at most {f.Int(2, 4)} visitors at the same time.",
                [new("weekday", "At what time do weekday visiting hours end at {0}?", 1)]),
            new("Intensive care", f => $"Intensive care visits are limited to {f.Int(10, 30)} minutes and one visitor at a time. Visitors must wear the gown provided at the unit door.",
                [new("icu", "How many minutes can a visitor stay in intensive care at {0}?", 0)]),
            new("Children", f => $"Children under {f.Int(7, 14)} may visit only together with an adult. Flowers and food from outside are not allowed in patient rooms.",
                [new("children", "Under what age must children be accompanied by an adult when visiting {0}?", 0)]),
        ]),
        new("parking", "Parking",
        [
            new("Car park", f => $"The car park has {f.Int(80, 600)} spaces and charges {f.Int(15, 90)} TL per hour. The first {f.Int(10, 30)} minutes are free of charge.",
                [new("fee", "How much does the car park at {0} charge per hour?", 1)]),
            new("Accessible parking", f => $"There are {f.Int(4, 24)} accessible parking spaces next to the {f.Pick(Gates)} entrance. They are free for holders of a disability parking card.",
                [new("accessible", "Next to which entrance are the accessible parking spaces at {0}?", 1)]),
            new("Bicycles", f => $"Bicycle racks for {f.Int(10, 60)} bicycles are located by the {f.Pick(Gates)} gate. The racks are covered and monitored by camera.",
                []),
        ]),
        new("lab-results", "Laboratory Results",
        [
            new("Turnaround times", f => $"Routine blood test results are ready within {f.Int(4, 48)} hours. Culture results take up to {f.Int(3, 7)} days.",
                [new("blood", "How many hours does it take for routine blood test results at {0}?", 0)]),
            new("Online access", f => $"Results are published on the patient portal, protected by a {f.Int(4, 8)}-digit code sent by SMS. The code expires after {f.Int(5, 30)} minutes.",
                [new("code", "How many digits does the SMS code for the patient portal at {0} have?", 0)]),
            new("Critical values", f => $"Critical values are telephoned to the requesting physician within {f.Int(15, 60)} minutes. Patients are not informed by SMS about critical values.",
                []),
        ]),
        new("appointments", "Appointment Cancellation",
        [
            new("Notice period", f => $"Appointments must be cancelled at least {f.Int(2, 48)} hours in advance. Cancellations can be made by phone or on the patient portal.",
                [new("notice", "How many hours in advance must an appointment at {0} be cancelled?", 0)]),
            new("Missed appointments", f => $"After {f.Int(2, 5)} missed appointments, online booking is suspended for {f.Int(7, 60)} days. Emergency care is never refused because of missed appointments.",
                [new("missed", "For how many days is online booking suspended after missed appointments at {0}?", 1)]),
        ]),
        new("fees", "Fees and Payment",
        [
            new("Private examination", f => $"A private examination costs {f.Money(450, 3200)} TL. The fee is paid at the registration desk before the examination.",
                [new("exam", "How much does a private examination cost at {0}?", 0)]),
            new("Medical reports", f => $"A medical report costs {f.Money(150, 900)} TL and is issued within {f.Int(1, 10)} working days.",
                [new("report", "Within how many working days is a medical report issued at {0}?", 1)]),
            new("Payment methods", f => $"Payments are accepted by card and bank transfer. Card payments in up to {f.Int(2, 9)} instalments are possible.",
                []),
        ]),
        new("pharmacy", "Pharmacy",
        [
            new("Opening hours", f => $"The in-house pharmacy is open from {f.Hour(7, 9)}:00 to {f.Hour(17, 23)}:00 every day. It is closed on the first day of religious holidays.",
                [new("open", "Until what time is the in-house pharmacy at {0} open?", 1)]),
            new("Prescriptions", f => $"Electronic prescriptions can be collected within {f.Int(3, 30)} days of issue. A prescription number and an identity document are required.",
                [new("rx", "Within how many days must an electronic prescription be collected at {0}?", 0)]),
        ]),
    ];

    /// <summary>All entity names the generator can produce, in a seed-independent canonical order.</summary>
    public static int MaxEntities => TownPrefixes.Length * TownSuffixes.Length * Kinds.Length * Branches.Length;

    /// <summary>Generates <paramref name="entities"/> entities (each with all topics but one) and their questions.</summary>
    public static GeneratedCorpus Generate(int entities, int seed = 2026)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entities, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(entities, MaxEntities);
        var random = new Random(seed);
        var documents = new List<SourceDocument>();
        var questions = new List<QaItem>();

        foreach (var entity in PickEntities(entities, random))
        {
            var omitted = random.Next(Topics.Length);
            var entitySlug = Slug(entity);
            for (var t = 0; t < Topics.Length; t++)
            {
                var topic = Topics[t];
                var facts = new FactSource(random);
                if (t == omitted)
                {
                    // Draw the facts anyway so the random stream (and every later entity) does not depend on which topic
                    // was omitted; then ask one question the corpus cannot answer.
                    foreach (var section in topic.Sections)
                    {
                        section.Body(facts);
                    }

                    var probe = topic.Sections.SelectMany(s => s.Questions).First();
                    questions.Add(new QaItem($"{entitySlug}-{topic.Slug}-unanswerable", string.Format(CultureInfo.InvariantCulture, probe.Template, entity), null, null));
                    continue;
                }

                var docId = $"{entitySlug}-{topic.Slug}";
                // '\n' rather than AppendLine: the corpus must be byte-identical on every operating system.
                var markdown = new StringBuilder().Append("# ").Append(entity).Append(" — ").Append(topic.Title).Append("\n\n");
                for (var s = 0; s < topic.Sections.Count; s++)
                {
                    var section = topic.Sections[s];
                    facts.Clear();
                    var body = section.Body(facts);
                    markdown.Append("## ").Append(section.Heading).Append('\n').Append(body).Append("\n\n");
                    foreach (var q in section.Questions)
                    {
                        questions.Add(new QaItem(
                            $"{docId}-{q.Key}",
                            string.Format(CultureInfo.InvariantCulture, q.Template, entity),
                            $"{docId}#{s:D4}",
                            facts.Drawn[q.AnswerFact]));
                    }
                }

                documents.Add(new SourceDocument(docId, $"{entity} — {topic.Title}", "synthetic", markdown.ToString().TrimEnd() + "\n"));
            }
        }

        return new GeneratedCorpus(documents, questions);
    }

    /// <summary>Lower-case ASCII slug: <c>Işıkova Sahil Clinic</c> → <c>isikova-sahil-clinic</c>.</summary>
    public static string Slug(string text) => string.Join('-', TextNormalizer.Tokens(text));

    private static IEnumerable<string> PickEntities(int count, Random random)
    {
        // Unique, seeded selection without materialising all combinations twice.
        var chosen = new HashSet<int>();
        while (chosen.Count < count)
        {
            var index = random.Next(MaxEntities);
            if (chosen.Add(index))
            {
                yield return EntityName(index);
            }
        }
    }

    private static string EntityName(int index)
    {
        var prefix = TownPrefixes[index % TownPrefixes.Length];
        index /= TownPrefixes.Length;
        var suffix = TownSuffixes[index % TownSuffixes.Length];
        index /= TownSuffixes.Length;
        var kind = Kinds[index % Kinds.Length];
        index /= Kinds.Length;
        var branch = Branches[index % Branches.Length];
        var town = prefix + suffix;
        return branch.Length == 0 ? $"{town} {kind}" : $"{town} {branch} {kind}";
    }

    internal sealed record Topic(string Slug, string Title, IReadOnlyList<Section> Sections);

    internal sealed record Section(string Heading, Func<FactSource, string> Body, IReadOnlyList<QuestionTemplate> Questions);

    /// <param name="AnswerFact">Index of the drawn fact (in drawing order within the section) that answers it.</param>
    internal sealed record QuestionTemplate(string Key, string Template, int AnswerFact);

    /// <summary>Draws facts from the shared random stream and remembers them as strings for the gold answers.</summary>
    internal sealed class FactSource(Random random)
    {
        public List<string> Drawn { get; } = [];

        public void Clear() => Drawn.Clear();

        public string Int(int min, int max) => Remember(random.Next(min, max + 1).ToString(CultureInfo.InvariantCulture));

        public string Hour(int min, int max) => Int(min, max);

        // Turkish price formatting: thousands separated by '.', always whole lira in this corpus.
        public string Money(int min, int max) =>
            Remember((random.Next(min / 10, (max / 10) + 1) * 10).ToString("#,0", CultureInfo.InvariantCulture).Replace(',', '.'));

        public string Pick(string[] options) => Remember(options[random.Next(options.Length)]);

        private string Remember(string value)
        {
            Drawn.Add(value);
            return value;
        }
    }
}
