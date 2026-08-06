namespace ViewEngineServer.WebApp.Http;

public sealed class IngestRequestDto
{
    public string Operation { get; set; } = "upsert";

    public Dictionary<string, string?>? Fields { get; set; }

    public string? PrimaryKeyValue { get; set; }
}