namespace Iyu.Core.Attributes;

/// <summary>
/// M3L `# Entity.Column` 바인딩에서 자동 생성되는 어트리뷰트.
/// 이 프로퍼티의 값이 지정 엔티티의 지정 컬럼에서 온다는 힌트를 제공한다.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class BindingAttribute : Attribute
{
    public string TargetEntity { get; }
    public string TargetColumn { get; }

    public BindingAttribute(string targetEntity, string targetColumn)
    {
        TargetEntity = targetEntity;
        TargetColumn = targetColumn;
    }
}
