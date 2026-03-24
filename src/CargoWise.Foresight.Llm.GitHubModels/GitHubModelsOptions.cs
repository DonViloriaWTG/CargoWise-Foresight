namespace CargoWise.Foresight.Llm.GitHubModels;

public sealed class GitHubModelsOptions
{
    public string Token { get; set; } = "";
    public string Model { get; set; } = "gpt-4o";
    public int TimeoutSeconds { get; set; } = 120;
}
