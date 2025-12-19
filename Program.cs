using System.ClientModel;
using OpenAI;
using OpenAI.Chat;
using System.Text.Json;
using CsvHelper;
using System.Globalization;
using CsvHelper.Configuration;

#pragma warning disable OPENAI001 // Ignore OpenAI experimental features warnings


var modelName = Environment.GetEnvironmentVariable("OPEN_AI_MODEL");
var baseUrl = Environment.GetEnvironmentVariable("OPEN_AI_HOST");
var credential = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var outputFilePath = Environment.GetEnvironmentVariable("OUTPUT_FILE_PATH");
var numPromptsStr = Environment.GetEnvironmentVariable("NUM_PROMPTS");

int numPrompts = 1;
if (!string.IsNullOrWhiteSpace(numPromptsStr) && int.TryParse(numPromptsStr, out var n))
{
    numPrompts = n;
}


if (string.IsNullOrWhiteSpace(modelName))
    throw new ArgumentException("Missing OPEN_AI_MODEL environment variable.");
if (string.IsNullOrWhiteSpace(baseUrl))
    throw new ArgumentException("Missing OPEN_AI_HOST environment variable.");
if (string.IsNullOrWhiteSpace(credential))
    throw new ArgumentException("Missing OPENAI_API_KEY environment variable.");
if (string.IsNullOrWhiteSpace(outputFilePath))
    throw new ArgumentException("Missing OUTPUT_FILE_PATH environment variable.");


static string Pick(Random rng, params string[] items) => items[rng.Next(items.Length)];

static string BuildSystemPrompt() =>
"""
You generate ONE standalone creative writing prompt for authors to use as inspiration. You are an expert at crafting engaging, non-formulaic prompts that spark imagination. You are also well-read in writing craft, story structure, the classics of literature, and creative writing techniques. Draw upon this knowledge to create high-quality prompts.

Output should be text only, no explanations or commentary, and no special characters apart from normal punctuation.

Hard rules:
- 1–3 concise sentences. No title. No list. No bullet points.
- Avoid formula openings like “Write a story about…”, “Imagine…”, “In a world…”.
- Avoid clichés (chosen one, it was all a dream, ancient prophecy, waking up and it was Tuesday, etc.).
- Prefer specific, concrete details (objects, textures, rules, sounds, smells).
- Include: (1) a clear situation, (2) a constraint/obstacle, (3) an emotional stake.
- Vary genre, time period, and narrative angle across outputs.
- Keep it fresh and non-formulaic.
- Simple is better than complex.

Output format:
- Return valid JSON ONLY, with exactly: {"prompt":"..."}.
- Do not include any other keys, commentary, or markdown.
- If the user message includes “Entropy:” or “Ingredients:”, NEVER repeat those words or the raw ingredient list.
""";

static string BuildUserPrompt(Random rng)
{
    // “Ingredients” nudge the model away from repeating the same template.
    // The entropy token helps avoid accidental caching/determinism upstream.
    string entropy = Guid.NewGuid().ToString("N");

    string genre = Pick(rng, "cozy mystery", "speculative sci-fi", "low fantasy", "gothic", "near-future", "historical oddity", "magical realism");
    //string lens  = Pick(rng, "a confession", "a warning label", "found information", "instruction", "a voicemail", "a failed performance review", "a recipe with footnotes");
    //string setting = Pick(rng, "a floodlit ferry deck at 2 a.m.", "a closed museum wing", "a salt marsh radio tower", "a motel ice machine alcove", "a town where clocks are illegal", "a greenhouse during a hailstorm");
    //string object1 = Pick(rng, "a warm coin that never cools", "a map drawn on bread", "a flute made of bone-colored glass", "a receipt that predicts tomorrow", "a key that locks doors open");
    //string object2 = Pick(rng, "a jar of unlabeled spices", "a pager that only beeps near liars", "a raincoat that smells like ozone", "a child’s sticker sheet missing one star", "a VHS tape with no images, only shadows");
    string constraint = Pick(rng, "no one can speak above a whisper", "every promise becomes physically heavy", "you must finish before sunrise or forget the reason", "each lie erases a color from the world", "touching metal causes time skips");
    string stake = Pick(rng, "someone is about to leave for good", "a friendship is quietly unraveling", "a debt must be paid without money", "a missing person returns with one condition", "a community is hiding a mercy");

    return $"""
Create ONE prompt that feels fresh and non-formulaic.

Ingredients (use at least 3, but DO NOT list them; weave them into the prompt naturally):
- Genre vibe: {genre}
- Constraint: {constraint}
- Emotional stake: {stake}

Entropy: {entropy}
""";
}

static int ScoreCandidate(string s)
{
    // crude but effective “anti-formula” scoring
    int score = 0;
    string t = s.Trim();

    if (t.Length is >= 120 and <= 380) score += 2;
    if (t.StartsWith("Write ", StringComparison.OrdinalIgnoreCase)) score -= 5;
    if (t.StartsWith("Imagine", StringComparison.OrdinalIgnoreCase)) score -= 5;
    if (t.Contains("In a world", StringComparison.OrdinalIgnoreCase)) score -= 4;
    if (t.Count(c => c == '\n') == 0) score += 1; // prefer single block
    if (t.Count(c => c == '.') + t.Count(c => c == '!') + t.Count(c => c == '?') <= 3) score += 1; // 1–3 sentences-ish

    // reward specificity
    if (t.Any(char.IsDigit)) score += 1;
    if (t.Contains("smell", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("sound", StringComparison.OrdinalIgnoreCase) ||
        t.Contains("texture", StringComparison.OrdinalIgnoreCase)) score += 1;

    return score;
}

static string? TryExtractPromptFromJson(string content)
{
    // content should be {"prompt":"..."} but be defensive
    try
    {
        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("prompt", out var p) &&
            p.ValueKind == JsonValueKind.String)
        {
            var output = p.GetString()?.Trim();
            return output;
        }

        // Not the expected format
        return null;
    }
    catch { 
        Console.WriteLine("Failed to parse JSON content.");
        return null;
    }
}

static async Task<List<string>> GenerateCandidatesAsync(
    ChatClient client,
    string modelName,
    string systemPrompt,
    string userPrompt,
    int n,
    CancellationToken ct)
{
    var messages = new List<ChatMessage>
    {
        new SystemChatMessage(systemPrompt),
        new UserChatMessage(userPrompt)
    };

    var options = new ChatCompletionOptions
    {
        Temperature = 1.35f,
        //TopP = 0.95f,             //if you tune this, usually don’t also tune temperature (pick one)
        PresencePenalty = 0.9f,
        FrequencyPenalty = 0.2f,
        MaxOutputTokenCount = 220,
        ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
        //ReasoningEffortLevel = ChatReasoningEffortLevel.High,  //not supported by llamma model, but is supported by others like gpt-oss

        //ResponseFormat = ChatResponseFormat.JsonObject
    };

    ChatCompletion completion = await client.CompleteChatAsync(messages, options, ct);

    List<string> candidates = new();
    foreach (var choice in completion.Content)
    {
        string? contentText = choice.Text;
        if (!string.IsNullOrWhiteSpace(contentText))
        {
            // Prefer extracting {"prompt": "..."} if we got JSON mode; otherwise keep raw.
            candidates.Add(TryExtractPromptFromJson(contentText) ?? contentText.Trim());
        }
    }

    return candidates;
}

static string? PickBestCandidate(IEnumerable<string> candidates, HashSet<string> seen)
{
    return candidates
        .Select(c => c.Trim())
        .Where(c => !c.Contains("{")) // discard json results that weren’t parsed properly
        .Where(c => !string.IsNullOrWhiteSpace(c))
        .Where(c => !seen.Contains(c))
        .OrderByDescending(c => ScoreCandidate(c))
        .FirstOrDefault();
}

// ----------------------------
// Your main loop (rewritten)
// ----------------------------
string systemPrompt = BuildSystemPrompt();

ChatClient client = new(
    model: modelName,
    credential: new ApiKeyCredential(credential),
    options: new OpenAIClientOptions() { Endpoint = new Uri(baseUrl) }
);

Console.WriteLine($"Using model: {modelName} at {baseUrl}");
Console.WriteLine("Generating creative writing prompts...");

var rng = Random.Shared;

// optional: load existing prompts to reduce duplicates across runs
var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
if (File.Exists(outputFilePath))
{
    using (var reader = new StreamReader(outputFilePath))
    using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
    {
        var records = csv.GetRecords<PromptRecord>();
        
        foreach (var record in records)
        {
            seen.Add(record.PromptText);
        }
    }
}

// using var streamWriter = new File.Open(outputFilePath, FileMode.Append);
// using var csvWriter = new CsvWriter(streamWriter, CultureInfo.InvariantCulture);
var config = new CsvConfiguration(CultureInfo.InvariantCulture)
    {
        // Don't write the header again.
        HasHeaderRecord = false,
    };
    
using (var stream = File.Open(outputFilePath, FileMode.Append))
using (var writer = new StreamWriter(stream))
using (var csvWriter = new CsvWriter(writer, config))
{

    // Write header if file is new (empty)
    if (new FileInfo(outputFilePath).Length == 0)
    {
        csvWriter.WriteHeader<PromptRecord>();
        await csvWriter.NextRecordAsync();
    }

    for (int i = 0; i < numPrompts; i++)
    {
        try
        {
            string userPrompt = BuildUserPrompt(rng);

            List<string> candidates = await GenerateCandidatesAsync(
                client,
                modelName,
                systemPrompt,
                userPrompt,
                n: 4,                  // multiple choices per request :contentReference[oaicite:3]{index=3}
                ct: CancellationToken.None
            );

            string? chosen = PickBestCandidate(candidates, seen);

            // If all choices duplicated or low-quality, do one quick retry (keeps it simple).
            if (string.IsNullOrWhiteSpace(chosen))
            {
                Console.WriteLine($"Prompt {i + 1}: all candidates low-quality or duplicates; skipping.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(chosen))
            {
                Console.WriteLine($"Prompt {i + 1}: got no usable candidate; skipping.");
                continue;
            }

            var record = new PromptRecord { PromptText = chosen };
            csvWriter.WriteRecord(record);
            csvWriter.Flush();
            await csvWriter.NextRecordAsync();
            await writer.FlushAsync();
            await stream.FlushAsync();

            seen.Add(chosen);
            Console.WriteLine($"Prompt {i + 1} written to file.");
        }
        catch (Exception ex)
        {
            // Keep going rather than crashing the full batch.
            Console.WriteLine($"Prompt {i + 1} failed: {ex.Message}");
        }
    }
}