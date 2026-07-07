namespace Iyu.VaultAi;

public class VaultAiSettings
{
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
    public Guid ReportAgentId { get; set; }
    public string ReportPath { get; set; } = "reports";

    /// <summary>SPA가 마운트되는 URL 경로. 기본값 "/vault-ai-reports".</summary>
    public string BasePath { get; set; } = "/vault-ai-reports";

    /// <summary>웹 뷰어 사이드바 앱 제목. 기본값 "Vault AI 리포트".</summary>
    public string Title { get; set; } = "Vault AI 리포트";

    /// <summary>리포트 말미 면책 문구에 표시할 데이터 출처. 예: "ICD MES Database"</summary>
    public string? DataSourceNote { get; set; }
}
