using System.Linq.Expressions;
using TabTabGo.Storage.Entities;

namespace TabTabGo.Storage.Services.Internal;

/// <summary>Adds <c>IsEnabled</c> to a caller's filter, so soft-deleted files do not show through inherited reads.</summary>
internal static class ActiveFilter
{
    public static Expression<Func<TFile, bool>> And<TFile>(Expression<Func<TFile, bool>>? filter)
        where TFile : class, IFile
    {
        var parameter = Expression.Parameter(typeof(TFile), "f");
        Expression active = Expression.Property(parameter, typeof(TFile).GetProperty(nameof(IFile.IsEnabled)) ?? typeof(IFile).GetProperty(nameof(IFile.IsEnabled))!);
        if (typeof(TFile).GetProperty(nameof(IFile.IsEnabled)) is null)
        {
            active = Expression.Property(Expression.Convert(parameter, typeof(IFile)), nameof(IFile.IsEnabled));
        }

        if (filter is null)
        {
            return Expression.Lambda<Func<TFile, bool>>(active, parameter);
        }

        var body = new Replacer(filter.Parameters[0], parameter).Visit(filter.Body)!;
        return Expression.Lambda<Func<TFile, bool>>(Expression.AndAlso(body, active), parameter);
    }

    private sealed class Replacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
