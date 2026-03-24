namespace CargoWise.Foresight.Api.Services;

public sealed class LlmProviderSettings
{
    private readonly object _lock = new();
    private string _provider;
    private string _model;
    private string _token = "";

    public LlmProviderSettings(string provider, string model)
    {
        _provider = provider;
        _model = model;
    }

    public (string Provider, string Model) Current
    {
        get { lock (_lock) return (_provider, _model); }
    }

    public string Token
    {
        get { lock (_lock) return _token; }
    }

    public void Update(string provider, string model, string? token = null)
    {
        lock (_lock)
        {
            _provider = provider;
            _model = model;
            if (token is not null)
                _token = token;
        }
    }
}
