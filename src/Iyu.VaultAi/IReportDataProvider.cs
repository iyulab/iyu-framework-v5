namespace Iyu.VaultAi;

/// <summary>
/// 리포트 프롬프트의 <c>{{data}}</c> 토큰에 주입할 사전 집계 데이터를 제공한다.
///
/// 소비앱(예: MES)이 구현해 DB의 검증된 SP/View 결과를 반환한다. 라이브러리는 데이터의
/// 출처(DB·연결 문자열·SP 이름 등)를 알지 못한다 — 도메인 로직은 전적으로 구현체에 산다.
///
/// 목적: LLM이 스키마를 추측하며 임의 SQL을 생성·반복 호출하는 대신, 정형 KPI를 미리 주입해
/// 도구 호출을 최소화하고 응답을 빠르고 안정적으로 만든다.
/// </summary>
public interface IReportDataProvider
{
    /// <summary>
    /// 주어진 리포트 폴더와 보고 기준일자에 대한 사전 집계 데이터를 반환한다.
    /// 반환 문자열(JSON/Markdown 등)이 프롬프트의 <c>{{data}}</c> 자리에 그대로 치환된다.
    /// 주입할 데이터가 없으면 <c>null</c>을 반환한다.
    /// </summary>
    /// <param name="folderPath">리포트 폴더 절대 경로(info.json·prompt.md가 위치).</param>
    /// <param name="reportDate">보고 기준일자.</param>
    Task<string?> GetReportDataAsync(string folderPath, DateTime reportDate, CancellationToken ct = default);
}
