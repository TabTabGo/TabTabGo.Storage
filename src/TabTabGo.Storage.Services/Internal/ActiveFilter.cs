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

    /// <summary>Both conditions must hold; a null <paramref name="second"/> leaves <paramref name="first"/> as it is.</summary>
    public static Expression<Func<TFile, bool>> AndAlso<TFile>(Expression<Func<TFile, bool>> first,
        Expression<Func<TFile, bool>>? second)
        where TFile : class, IFile
    {
        if (second is null)
        {
            return first;
        }

        var parameter = first.Parameters[0];
        var body = new Replacer(second.Parameters[0], parameter).Visit(second.Body)!;
        return Expression.Lambda<Func<TFile, bool>>(Expression.AndAlso(first.Body, body), parameter);
    }

    private sealed class Replacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
