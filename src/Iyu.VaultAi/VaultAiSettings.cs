namespace Iyu.VaultAi;

public class VaultAiSettings
{
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";

    /// <summary>
    /// true 이면 서버 인증서 검증을 끈다(어떤 인증서든 받아들인다). 기본값 false — 운영체제 신뢰 저장소로
    /// 서버 인증서를 검증한다. 자체 서명 인증서를 쓰는 내부 서버에 붙을 때만 켠다: 켜면 연결 상대가
    /// 진짜 서버인지 확인되지 않아 <see cref="Token"/> 이 가로채일 수 있다. 가능하면 서버 인증서의 발급
    /// 기관을 운영체제 신뢰 저장소에 추가하는 쪽을 권한다.
    /// </summary>
    public bool AllowUntrustedServerCertificate { get; set; }

    /// <summary>
    /// Name of the authorization policy every route of the module is held to — the report API, the viewer, and the
    /// report folders served at <c>{BasePath}-files</c>. Empty (the default) means any signed-in user. The policy must
    /// be registered with <c>AddAuthorization</c>; a name that is not registered fails at <c>UseVaultAiReports()</c>.
    /// Reading the reports and generating, editing or deleting them share this one policy.
    /// </summary>
    public string? AuthorizationPolicy { get; set; }

    /// <summary>
    /// true serves the module to anonymous callers, which also lets them start report generation (an LLM call),
    /// edit and delete reports and read the report folders. Default false. Turn it on only when something in front of
    /// the host — a network boundary or a reverse proxy that authenticates — already decides who gets here.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// The chat channel the browser widget embeds. Blank (the default) leaves <c>MapVaultAiEmbed()</c> answering
    /// "not enabled": the server-side proxy for the widget's short-lived token is only active when a channel is named.
    /// </summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// The address the browser loads the widget from, returned by <c>GET {path}/config</c>. Blank (the default) means
    /// <see cref="Url"/>. Set it when <see cref="Url"/> is an address only the server can reach — it is returned to
    /// every caller the module's authorization admits.
    /// </summary>
    public string? EmbedOrigin { get; set; }

    /// <summary>
    /// How long the server waits for the chat service to issue the widget's token before answering <c>502</c>. Default
    /// ten seconds: a slow chat service must not hold the page's widget request open for the client's default timeout.
    /// </summary>
    public TimeSpan EmbedTokenTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long one call to the vault-ai server may take before the client gives up with
    /// <see cref="VaultAiTimeoutException"/>. Default ten minutes — the limit the client has always had. A caller can
    /// pass a limit of its own to the overloads taking a <see cref="TimeSpan"/>, shorter or longer. Has to be greater
    /// than zero; a value that is not fails at host start.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public Guid ReportAgentId { get; set; }
    public string ReportPath { get; set; } = "reports";

    /// <summary>SPA가 마운트되는 URL 경로. 기본값 "/vault-ai-reports".</summary>
    public string BasePath { get; set; } = "/vault-ai-reports";

    /// <summary>웹 뷰어 사이드바 앱 제목. 기본값 "Vault AI 리포트".</summary>
    public string Title { get; set; } = "Vault AI 리포트";

    /// <summary>리포트 말미 면책 문구에 표시할 데이터 출처. 예: "ICD MES Database"</summary>
    public string? DataSourceNote { get; set; }
}
