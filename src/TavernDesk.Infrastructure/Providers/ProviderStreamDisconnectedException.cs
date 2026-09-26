namespace TavernDesk.Infrastructure.Providers;

public sealed class ProviderStreamDisconnectedException : IOException
{
    public ProviderStreamDisconnectedException()
        : base("模型响应流在收到结束标志或结束原因前关闭，已收到的正文可能不完整。")
    {
    }
}
