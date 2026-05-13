namespace Iyu.Core.Entities;

/// <summary>
/// 사용자 정의 범주형 목록 항목 마커 인터페이스.
/// 이 인터페이스를 구현한 엔티티는 프레임워크가 선택 가능한 값 목록으로 인식한다.
/// Key = DB 저장값, Display = 표시값(없으면 Key), Description = 짧은 설명.
/// </summary>
public interface IUserMasterList
{
    string Key { get; }
    string? Display { get; }
    string? Description { get; }
}
