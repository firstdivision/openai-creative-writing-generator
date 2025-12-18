using System.ClientModel;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

var modelName = Environment.GetEnvironmentVariable("OPEN_AI_MODEL");
var baseUrl = Environment.GetEnvironmentVariable("OPEN_AI_HOST");
var credential = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

//check that all of the above variables are set
if (string.IsNullOrEmpty(modelName))
{
    throw new ArgumentNullException("OPEN_AI_MODEL environment variable is not set.");
}   
if (string.IsNullOrEmpty(baseUrl))
{
    throw new ArgumentNullException("OPEN_AI_HOST environment variable is not set.");
}
if (string.IsNullOrEmpty(credential))
{
    throw new ArgumentNullException("OPENAI_API_KEY environment variable is not set.");
}

ChatClient client = new(
    model: modelName,
    credential: new ApiKeyCredential(credential),
    options: new OpenAIClientOptions()
    {
        Endpoint = new Uri(baseUrl)
    }
);

Console.WriteLine($"Using model: {modelName} at {baseUrl}");
Console.WriteLine("Generating creative writing prompts...");

for (int i = 0; i < 100; i++)
{
    try
    {
        ChatCompletion completion = await client.CompleteChatAsync("Please create a creative writing prompt. Only include the creative writing prompt in your response, no additional text.");
        
        if (completion == null || completion.Content.Any() == false)
        {
            Console.WriteLine("Received empty response from the model.");
            continue;
        }
        Console.WriteLine(completion.Content.First().Text);
    }
    catch (System.Exception)
    {
        throw;
    }
}




