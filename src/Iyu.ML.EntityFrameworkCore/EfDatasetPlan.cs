using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace Iyu.ML.EntityFrameworkCore;

/// <summary>One declared dataset in the form the source reads it, without the entity type showing.</summary>
internal interface IEfDatasetPlan
{
    MLDatasetDefinition Definition { get; }

    /// <summary>The entity the rows come from; what the read guard is asked about.</summary>
    Type EntityType { get; }

    /// <summary>Every property the dataset reads: its columns and the properties its filter names. Each is a read.</summary>
    IReadOnlyList<EfDatasetRead> Reads { get; }

    IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadAsync(DbContext context, CancellationToken cancellationToken);
}

/// <summary>A property a dataset reads, and what it reads it as.</summary>
internal sealed record EfDatasetRead(string Property, string Use);

internal sealed class EfDatasetPlan<TEntity> : IEfDatasetPlan where TEntity : class
{
    private readonly IReadOnlyList<MLDatasetBuilder<TEntity>.ColumnBinding> _columns;
    private readonly Expression<Func<TEntity, bool>>? _filter;
    private readonly Expression<Func<TEntity, object?[]>> _projection;

    public EfDatasetPlan(
        MLDatasetDefinition definition,
        IReadOnlyList<MLDatasetBuilder<TEntity>.ColumnBinding> columns,
        Expression<Func<TEntity, bool>>? filter)
    {
        Definition = definition;
        _columns = columns.ToList();
        _filter = filter;

        // One projection that names only the declared columns, so a property the dataset does not declare is never selected
        // from the database — not read and then left out.
        var parameter = Expression.Parameter(typeof(TEntity), "e");
        var values = _columns.Select(c => new MLDatasetBuilder<TEntity>.ParameterReplacer(c.Selector.Parameters[0], parameter).Visit(c.Selector.Body));
        _projection = Expression.Lambda<Func<TEntity, object?[]>>(Expression.NewArrayInit(typeof(object), values), parameter);

        var reads = _columns.Select(c => new EfDatasetRead(c.Property.Name, c.Role == MLColumnRole.Label ? "label" : "feature")).ToList();
        if (filter is not null)
        {
            var named = new FilterReads(filter.Parameters[0]);
            named.Visit(filter.Body);
            reads.AddRange(named.Names.Distinct().Select(n => new EfDatasetRead(n, "filter")));
        }
        Reads = reads;
    }

    public MLDatasetDefinition Definition { get; }

    public Type EntityType => typeof(TEntity);

    public IReadOnlyList<EfDatasetRead> Reads { get; }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadAsync(
        DbContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IQueryable<TEntity> query = context.Set<TEntity>().AsNoTracking();
        if (_filter is not null)
            query = query.Where(_filter);

        await foreach (var values in query.Select(_projection).AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(_columns.Count, StringComparer.Ordinal);
            for (var i = 0; i < _columns.Count; i++)
                row[_columns[i].Column] = values[i];
            yield return row;
        }
    }
}
