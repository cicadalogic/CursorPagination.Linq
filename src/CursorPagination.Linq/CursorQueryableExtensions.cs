using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace System.Linq.CursorPagination
{
    using System.Linq;
    using System.Linq.Expressions;

    /// <summary>
    ///     Extends <see cref="IQueryable{T}"/> to support cursor pagination.
    /// </summary>
    public static class CursorQueryableExtensions
    {
        /// <summary>
        ///     Converts an <see cref="IQueryable{T}"/> to a cursor connection source.
        /// </summary>
        /// <remarks>
        ///     If you use <see cref="Queryable.Take{TSource}(IQueryable{TSource}, int)"/> with a negative value,
        ///     the source will be reversed and take will be replaced with its absolute value.
        /// </remarks>
        /// <typeparam name="T">The type of the data in the data source.</typeparam>
        /// <param name="source">The source to convert.</param>
        /// <returns>
        ///     A new cursor source.
        /// </returns>
        public static IQueryable<T> AsConnection<T>(this IQueryable<T> source)
        {
            var cursorExpression = CursorExpressionVisitor<T>.Visit(source.Expression, null, false);

            return source.Provider.CreateQuery<T>(cursorExpression);
        }

        /// <summary>
        ///     Converts an <see cref="IQueryable{T}"/> to a cursor connection and creates a <see cref="List{T}"/> from it.
        /// </summary>
        /// <remarks>
        ///     The returned list will preserve its direction even when backwards.
        /// </remarks>
        /// <typeparam name="T">The type of the data in the data source.</typeparam>
        /// <param name="source">The source to convert.</param>
        /// <returns>
        ///     A <see cref="List{T}"/> created from the source.
        /// </returns>
        public static List<T> ToNodeList<T>(this IQueryable<T> source)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            var cursorExpression = CursorExpressionVisitor<T>.Visit(source.Expression, null, false);

            var nodes = source.Provider.CreateQuery<T>(cursorExpression).ToList();

            // restore the order
            if (cursorExpression.Backwards)
            {
                nodes.Reverse();
            }

            return nodes;
        }

        /// <summary>
        ///     Converts an <see cref="IQueryable{T}"/> to a cursor connection and creates a <see cref="List{T}"/> by enumerating it asynchronously.
        /// </summary>
        /// <remarks>
        ///     The returned list will preserve its direction even when backwards.
        /// </remarks>
        /// <typeparam name="T">The type of the data in the data source.</typeparam>
        /// <param name="source">The source to convert.</param>
        /// <param name="cancellationToken"> A <see cref="CancellationToken" /> to observe while waiting for the task to complete. </param>
        /// <returns>
        ///     A <see cref="List{T}"/> created from the source.
        /// </returns>
        public static async Task<List<T>> ToNodeListAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            var cursorExpression = CursorExpressionVisitor<T>.Visit(source.Expression, null, false);

            if (source.Provider.CreateQuery<T>(cursorExpression) is not IAsyncEnumerable<T> asyncEnumerable)
            {
                throw new InvalidOperationException($"The source 'IQueryable' doesn't implement 'IAsyncEnumerable<{typeof(T)}>'.");
            }

            List<T> nodes = new();
            await foreach (T node in asyncEnumerable.WithCancellation(cancellationToken))
            {
                nodes.Add(node);
            }

            // restore the order
            if (cursorExpression.Backwards)
            {
                nodes.Reverse();
            }

            return nodes;
        }

        /// <summary>
        ///     Creates an <see cref="IConnection{TNode}"/> from the source.
        /// </summary>
        /// <typeparam name="T">The type of the data in the data source.</typeparam>
        /// <param name="source">The source to convert.</param>
        /// <returns>
        ///     A <see cref="IConnection{TNode}"/> created from the source.
        /// </returns>
        public static IConnection<T> ToConnection<T>(this IQueryable<T> source) => ToConnection(source, null, false);

        /// <summary>
        ///     Creates an <see cref="IConnection{TNode}"/> from the source.
        /// </summary>
        /// <typeparam name="T">The type of the data in the data source.</typeparam>
        /// <param name="source">The source to convert.</param>
        /// <param name="cursorProvider"> The cursor provider that can set or overwrite the source cursor. </param>
        /// <param name="withTotalCount"> True if the total count should be computed. </param>
        /// <returns>
        ///     A <see cref="IConnection{TNode}"/> created from the source.
        /// </returns>
        public static IConnection<T> ToConnection<T>(this IQueryable<T> source, CursorProvider? cursorProvider = null, bool withTotalCount = false)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            var cursorExpression = CursorExpressionVisitor<T>.Visit(source.Expression, cursorProvider, true);

            int? totalCount = withTotalCount ? source.Provider.Execute<int>(cursorExpression.TotalCount) : null;
            bool hasPrevious = cursorExpression.HasPrevious is not null && source.Provider.Execute<bool>(cursorExpression.HasPrevious);

            List<IEdge<T>> edges = new();
            foreach (T node in source.Provider.CreateQuery<T>(cursorExpression))
            {
                edges.Add(new Edge<T>(cursorExpression.GetCursor(node), node));
            }

            return CreateConnection(edges, cursorExpression.Take, cursorExpression.Backwards, totalCount, hasPrevious);
        }

        /// <summary>
        ///     Creates a <see cref="IConnection{TNode}" /> from an <see cref="IQueryable{T}" /> by enumerating it asynchronously.
        /// </summary>
        /// <typeparam name="T"> The type of the elements of <paramref name="source" />. </typeparam>
        /// <param name="source"> An <see cref="IQueryable{T}" /> to create a <see cref="IConnection{TNode}" /> from. </param>
        /// <param name="cancellationToken"> A <see cref="CancellationToken" /> to observe while waiting for the task to complete. </param>
        /// <returns>
        ///     A task that represents the asynchronous operation.
        ///     The task result contains a <see cref="IConnection{TNode}" /> that contains the result of the operation.
        /// </returns>
        public static Task<IConnection<T>> ToConnectionAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default)
            => ToConnectionAsync(source, null, false, cancellationToken);

        /// <summary>
        ///     Creates a <see cref="IConnection{TNode}" /> from an <see cref="IQueryable{T}" /> by enumerating it asynchronously.
        /// </summary>
        /// <typeparam name="T"> The type of the elements of <paramref name="source" />. </typeparam>
        /// <param name="source"> An <see cref="IQueryable{T}" /> to create a <see cref="IConnection{TNode}" /> from. </param>
        /// <param name="cursorProvider"> The cursor provider that can set or overwrite the source cursor. </param>
        /// <param name="withTotalCount"> True if a total count should be computed. </param>
        /// <param name="cancellationToken"> A <see cref="CancellationToken" /> to observe while waiting for the task to complete. </param>
        /// <returns>
        ///     A task that represents the asynchronous operation.
        ///     The task result contains a <see cref="IConnection{TNode}" /> that contains the result of the operation.
        /// </returns>
        public static async Task<IConnection<T>> ToConnectionAsync<T>(this IQueryable<T> source, CursorProvider? cursorProvider = null, bool withTotalCount = false, CancellationToken cancellationToken = default)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));

            var cursorExpression = CursorExpressionVisitor<T>.Visit(source.Expression, cursorProvider, true);

            if (source.Provider.CreateQuery<T>(cursorExpression) is not IAsyncEnumerable<T> asyncEnumerable)
            {
                throw new InvalidOperationException($"The source 'IQueryable' doesn't implement 'IAsyncEnumerable<{typeof(T)}>'.");
            }

            int? totalCount;
            bool hasPrevious;

            // well... this only check in the entire library force us to pull Microsoft.EntityFrameworkCore...
            if (source.Provider is Microsoft.EntityFrameworkCore.Query.IAsyncQueryProvider provider)
            {
                totalCount = withTotalCount ? await provider.ExecuteAsync<Task<int>>(cursorExpression.TotalCount, cancellationToken) : null;
                hasPrevious = cursorExpression.HasPrevious is not null && await provider.ExecuteAsync<Task<bool>>(cursorExpression.HasPrevious, cancellationToken);
            }
            else
            {
                totalCount = withTotalCount ? source.Provider.Execute<int>(cursorExpression.TotalCount) : null;
                hasPrevious = cursorExpression.HasPrevious is not null && source.Provider.Execute<bool>(cursorExpression.HasPrevious);

                cancellationToken.ThrowIfCancellationRequested();
            }

            List<IEdge<T>> edges = new();
            await foreach (T node in asyncEnumerable.WithCancellation(cancellationToken))
            {
                edges.Add(new Edge<T>(cursorExpression.GetCursor(node), node));
            }

            return CreateConnection(edges, cursorExpression.Take, cursorExpression.Backwards, totalCount, hasPrevious);
        }

        private static IConnection<T> CreateConnection<T>(List<IEdge<T>> edges, int? take, bool backwards, int? totalCount, bool hasPrevious)
        {
            bool hasNext;

            // assume Peek is true
            if (take.HasValue)
            {
                Debug.Assert(take.Value > 0); // cannot be 0, since we have peek'd

                if (edges.Count > take.Value)
                {
                    hasNext = true;
                    edges.RemoveAt(edges.Count - 1);
                }
                else
                {
                    hasNext = false;
                }
            }
            else
            {
                hasNext = false;
            }

            // restore the order
            if (backwards)
            {
                edges.Reverse();
            }

            IEdge<T>? firstEdge;
            IEdge<T>? lastEdge;
            if (edges.Count > 0)
            {
                firstEdge = edges[0];
                lastEdge = edges[edges.Count - 1];
            }
            else
            {
                firstEdge = null;
                lastEdge = null;
            }

            return new Connection<T>(totalCount, new PageInfo(hasNext, hasPrevious, firstEdge?.Cursor, lastEdge?.Cursor), edges);
        }
    }

    /// <summary>
    ///     Enumerates the last calls of
    ///     <list type="bullet">
    ///         <item><see cref="Queryable.OrderBy{TSource, TKey}(IQueryable{TSource}, Expression{Func{TSource, TKey}})"/></item>
    ///         <item><see cref="Queryable.OrderByDescending{TSource, TKey}(IQueryable{TSource}, Expression{Func{TSource, TKey}})"/></item>
    ///         <item><see cref="Queryable.ThenBy{TSource, TKey}(IOrderedQueryable{TSource}, Expression{Func{TSource, TKey}})"/></item>
    ///         <item><see cref="Queryable.ThenByDescending{TSource, TKey}(IOrderedQueryable{TSource}, Expression{Func{TSource, TKey}})"/></item>
    ///     </list>
    ///     Enumerates and remove the last calls of
    ///     <list type="bullet">
    ///         <item><see cref="Queryable.Skip{TSource}(IQueryable{TSource}, int)"/></item>
    ///         <item><see cref="Queryable.Take{TSource}(IQueryable{TSource}, int)"/></item>
    ///         <item><see cref="Queryable.TakeLast{TSource}(IQueryable{TSource}, int)"/></item>
    ///     </list>
    ///     Create the cursror from and remove
    ///     <list type="bullet">
    ///         <item><see cref="Queryable.TakeWhile{TSource}(IQueryable{TSource}, Expression{Func{TSource, bool}})"/></item>
    ///     </list>
    ///     Do not use as they are not supported
    ///     <list type="bullet">
    ///         <item><see cref="Queryable.SkipLast{TSource}(IQueryable{TSource}, int)"/></item>
    ///         <item><see cref="Queryable.TakeWhile{TSource}(IQueryable{TSource}, Expression{Func{TSource, int, bool}})"/></item>
    ///         <item><see cref="Queryable.SkipWhile{TSource}(IQueryable{TSource}, Expression{Func{TSource, bool}})"/></item>
    ///         <item><see cref="Queryable.SkipWhile{TSource}(IQueryable{TSource}, Expression{Func{TSource, int, bool}})"/></item>
    ///     </list>
    /// </summary>
    public sealed class CursorExpressionVisitor<T> : ExpressionVisitor
    {
        #region CachedReflection
        // see: https://github.com/dotnet/runtime/blob/main/src/libraries/System.Linq.Queryable/src/System/Linq/CachedReflection.cs

        private static MethodInfo? s_TakeLast_TSource_2;

        private static MethodInfo TakeLast_TSource_2(Type TSource) =>
             (s_TakeLast_TSource_2 ??= new Func<IQueryable<object>, int, IQueryable<object>>(Queryable.TakeLast).GetMethodInfo().GetGenericMethodDefinition())
              .MakeGenericMethod(TSource);

        private static MethodInfo? s_Take_Int32_TSource_2;

        private static MethodInfo Take_Int32_TSource_2(Type TSource) =>
             (s_Take_Int32_TSource_2 ??= new Func<IQueryable<object>, int, IQueryable<object>>(Queryable.Take).GetMethodInfo().GetGenericMethodDefinition())
              .MakeGenericMethod(TSource);

        private static MethodInfo? s_TakeWhile_TSource_2;

        private static MethodInfo TakeWhile_TSource_2(Type TSource) =>
             (s_TakeWhile_TSource_2 ??= new Func<IQueryable<object>, Expression<Func<object, bool>>, IQueryable<object>>(Queryable.TakeWhile).GetMethodInfo().GetGenericMethodDefinition())
              .MakeGenericMethod(TSource);

        private static MethodInfo? s_Skip_TSource_2;

        private static MethodInfo Skip_TSource_2(Type TSource) =>
             (s_Skip_TSource_2 ??= new Func<IQueryable<object>, int, IQueryable<object>>(Queryable.Skip).GetMethodInfo().GetGenericMethodDefinition())
              .MakeGenericMethod(TSource);

        private static MethodInfo? s_Where_TSource_2;

        private static MethodInfo Where_TSource_2(Type TSource) =>
             (s_Where_TSource_2 ??= new Func<IQueryable<object>, Expression<Func<object, bool>>, IQueryable<object>>(Queryable.Where).GetMethodInfo().GetGenericMethodDefinition())
              .MakeGenericMethod(TSource);

        private static MethodInfo? s_Count_TSource_1;

        private static MethodInfo Count_TSource_1(Type TSource) =>
             (s_Count_TSource_1 ??= new Func<IQueryable<object>, int>(Queryable.Count).GetMethodInfo().GetGenericMethodDefinition())
              .MakeGenericMethod(TSource);

        private static MethodInfo? s_Any_TSource_1;

        private static MethodInfo Any_TSource_1(Type TSource) =>
             (s_Any_TSource_1 ??= new Func<IQueryable<object>, bool>(Queryable.Any).GetMethodInfo().GetGenericMethodDefinition())
              .MakeGenericMethod(TSource);

        private static MethodInfo? s_Reverse_TSource_1;

        private static MethodInfo Reverse_TSource_1(Type TSource) =>
             (s_Reverse_TSource_1 ??= new Func<IQueryable<object>, IQueryable<object>>(Queryable.Reverse).GetMethodInfo().GetGenericMethodDefinition())
              .MakeGenericMethod(TSource);

        #endregion

        #region IsOrderingMethod

        private static MethodInfo? s_OrderBy_TSource_TKey_2;

        private static MethodInfo OrderBy_TSource_TKey_2() =>
             (s_OrderBy_TSource_TKey_2 ??= new Func<IQueryable<object>, Expression<Func<object, object>>, IOrderedQueryable<object>>(Queryable.OrderBy).GetMethodInfo().GetGenericMethodDefinition());

        private static MethodInfo? s_OrderByDescending_TSource_TKey_2;

        private static MethodInfo OrderByDescending_TSource_TKey_2() =>
             (s_OrderByDescending_TSource_TKey_2 ??= new Func<IQueryable<object>, Expression<Func<object, object>>, IOrderedQueryable<object>>(Queryable.OrderByDescending).GetMethodInfo().GetGenericMethodDefinition());

        private static MethodInfo? s_ThenBy_TSource_TKey_2;

        private static MethodInfo ThenBy_TSource_TKey_2() =>
             (s_ThenBy_TSource_TKey_2 ??= new Func<IOrderedQueryable<object>, Expression<Func<object, object>>, IOrderedQueryable<object>>(Queryable.ThenBy).GetMethodInfo().GetGenericMethodDefinition());

        private static MethodInfo? s_ThenByDescending_TSource_TKey_2;

        private static MethodInfo ThenByDescending_TSource_TKey_2() =>
             (s_ThenByDescending_TSource_TKey_2 ??= new Func<IOrderedQueryable<object>, Expression<Func<object, object>>, IOrderedQueryable<object>>(Queryable.ThenByDescending).GetMethodInfo().GetGenericMethodDefinition());

        private bool IsOrderingMethod(MethodCallExpression expression)
        {
            var m = expression.Method.GetGenericMethodDefinition();

            return m == OrderBy || m == OrderByDescending || m == ThenBy || m == ThenByDescending;
        }

        #endregion

        /// <summary> Local evaluation of the call expression. </summary>
        private static TValue GetExpressionValue<TValue>(MethodCallExpression expression)
        {
            // do not convert the expression, we use this on know types only
            // m = expression.Arguments[1];
            // m = Expression.Convert(m, typeof(T));

            if (expression.Arguments[1] is ConstantExpression constantExpression)
            {
                return (TValue)constantExpression.Value!;
            }
            else
            {
                return Expression.Lambda<Func<TValue>>(expression.Arguments[1]).Compile()();
            }
        }

        // cache those MethodInfo
        private readonly MethodInfo Skip;
        private readonly MethodInfo Take;
        private readonly MethodInfo TakeLast;
        private readonly MethodInfo TakeWhile;

        private readonly MethodInfo OrderBy;
        private readonly MethodInfo OrderByDescending;
        private readonly MethodInfo ThenBy;
        private readonly MethodInfo ThenByDescending;

        private readonly MethodInfo Where;
        private readonly MethodInfo Reverse;
        private readonly MethodInfo Count;
        private readonly MethodInfo Any;

        private readonly Dictionary<string, MethodCallExpression> orderByExpressions = new();
        private readonly Dictionary<string, MethodCallExpression> takeWhileExpressions = new();
        private MethodCallExpression? takeExpression; private bool isTakeLast;
        private MethodCallExpression? skipExpression;

        /// <summary>
        ///     Initialize a new <see cref="CursorExpressionVisitor{T}"/>.
        /// </summary>
        private CursorExpressionVisitor()
        {
            Type type = typeof(T);

            Skip = Skip_TSource_2(type);
            Take = Take_Int32_TSource_2(type);
            TakeLast = TakeLast_TSource_2(type);
            TakeWhile = TakeWhile_TSource_2(type);

            // we do not know the key type, we could compare only the first generic but for now leave as it its
            OrderBy = OrderBy_TSource_TKey_2();
            OrderByDescending = OrderByDescending_TSource_TKey_2();
            ThenBy = ThenBy_TSource_TKey_2();
            ThenByDescending = ThenByDescending_TSource_TKey_2();

            Where = Where_TSource_2(type);
            Reverse = Reverse_TSource_1(type);
            Count = Count_TSource_1(type);
            Any = Any_TSource_1(type);
        }

        /// <inheritdoc />
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            MethodInfo method = node.Method;

            if (method.DeclaringType == typeof(Queryable))
            {
                MethodInfo m = method;

                if (m == Skip)
                {
                    if (skipExpression is null)
                    {
                        skipExpression = node;

                        // remove this node, we compute it later
                        return NextNode(node);
                    }
                }

                else if (m == Take || m == TakeLast)
                {
                    if (takeExpression is null)
                    {
                        isTakeLast = m == TakeLast;
                        takeExpression = node;

                        // remove this node, we compute it later
                        return NextNode(node);
                    }
                }

                else if (m == TakeWhile)
                {
                    if (node.Arguments[1] is UnaryExpression u &&
                        u.Operand is LambdaExpression lambda &&
                        lambda.Body is BinaryExpression bx &&
                        bx.Left is MemberExpression mx &&
                        mx.Member is PropertyInfo or FieldInfo)
                    {
                        var memberName = mx.Member.Name;

                        if (!takeWhileExpressions.ContainsKey(memberName))
                        {
                            takeWhileExpressions.Add(memberName, node);

                            // remove this node, we compute it later
                            return NextNode(node);
                        }
                    }
                }

                else if (IsOrderingMethod(node))
                {
                    if (node.Arguments[1] is UnaryExpression u
                        && u.Operand is LambdaExpression lambda
                        && lambda.Body is MemberExpression mx
                        && mx.Member is PropertyInfo or FieldInfo)
                    {
                        var memberName = mx.Member.Name;

                        if (!orderByExpressions.ContainsKey(memberName))
                        {
                            orderByExpressions.Add(memberName, node);
                        }
                    }
                }
            }

            return base.VisitMethodCall(node);
        }

        /// <summary> Get the upwards expression of the node. </summary>
        private Expression NextNode(MethodCallExpression node)
        {
            if (node.Arguments[0] is MethodCallExpression mc)
            {
                // make sure the clear this node
                return VisitMethodCall(mc);
            }

            // go upwards
            return node.Arguments[0];
        }

        // ideally we want to expose this directly but it is not possible
        /// <summary>
        ///     Create a new <see cref="CursorExpression{T}"/> from the query expression.
        /// </summary>
        /// <param name="node"> The expression to visit. </param>
        /// <param name="cursorProvider"> The cursor provider for expression. </param>
        /// <param name="peek"> True when it should peek past the end of the sequence to see if there are any more elements. </param>
        /// <returns>
        ///     The <see cref="CursorExpression{T}"/> representing the cursor operation for the expression.
        /// </returns>
        [return: NotNullIfNotNull("node")]
        private CursorExpression<T>? VisitInternal(Expression? node, CursorProvider? cursorProvider, bool peek)
        {
            if (node is null) return null;

            // reset the visitor state
            orderByExpressions.Clear();
            takeWhileExpressions.Clear();

            takeExpression = null; isTakeLast = false;
            skipExpression = null;

            // visit the node
            node = Visit(node);

            // evaluate locally
            int skip = skipExpression is null ? 0 : GetExpressionValue<int>(skipExpression);
            int? take = takeExpression is null ? null : GetExpressionValue<int>(takeExpression);

            // flip for take last, it is basically Take with a negative value
            if (isTakeLast && take.HasValue) take = take.Value * -1;

            // TODO: provide a way to set this even when take is null
            // are we going backwards?
            bool backwards = take.HasValue && take.Value < 0;

            // special case, if we skip by 1 we can just use logical
            // operators to actually skip the cursor
            bool skipCursor = skip == 1;

            Expression sourceExpression = node;
            Expression? hasPreviousExpression = null;

            if (orderByExpressions.Count > 0)
            {
                foreach (var orderByPair in orderByExpressions)
                {
                    // visit this OrderBy Expressions
                    if (!TryVisitOrderingExpression(
                        orderByPair.Key,
                        orderByPair.Value,
                        cursorProvider,
                        out var parameter,
                        out var left,
                        out var right
                    ))
                    {
                        // we have no TakeWhile and no visitor
                        continue;
                    }

                    var orderBy = orderByPair.Value;
                    bool isDescending = orderBy.Method.Name.EndsWith("Descending");

                    ExpressionType binaryType;
                    ExpressionType hasPreviousBinaryType;

                    // this code looks more confusing than what it actually is
                    // when isDescending, we are going down so lets use the < LessThan operator
                    // meaning that binaryType will be < LessThan
                    // flip when run backwards
                    // if we need to include the cursor, use *OrEqual, for example LessThanOrEqual
                    // hasPrevious must be the inverse operator
                    if (isDescending)
                    {
                        binaryType = skipCursor
                            ? (backwards ? ExpressionType.GreaterThan : ExpressionType.LessThan)
                            : (backwards ? ExpressionType.GreaterThanOrEqual : ExpressionType.LessThanOrEqual);

                        hasPreviousBinaryType = skipCursor
                            ? (backwards ? ExpressionType.LessThanOrEqual : ExpressionType.GreaterThanOrEqual)
                            : (backwards ? ExpressionType.LessThan : ExpressionType.GreaterThan);
                    }
                    else
                    {
                        binaryType = skipCursor
                            ? (backwards ? ExpressionType.LessThan : ExpressionType.GreaterThan)
                            : (backwards ? ExpressionType.LessThanOrEqual : ExpressionType.GreaterThanOrEqual);

                        hasPreviousBinaryType = skipCursor
                            ? (backwards ? ExpressionType.GreaterThanOrEqual : ExpressionType.LessThanOrEqual)
                            : (backwards ? ExpressionType.GreaterThan : ExpressionType.LessThan);
                    }

                    // create the comparison expression body (x.Id < cursorValue)
                    var binaryExpression = Expression.MakeBinary(binaryType, left, right);

                    // create the labmda (x => (x.Id < cursorValue))
                    var lambda = Expression.Lambda(binaryExpression, parameter);

                    // here comes the magic that makes all work together nicely, beautifully, without any bugs whatsoever
                    // we basically create a where calause, sorted correctly
                    sourceExpression = Expression.Call(
                         null,
                         Where,
                         sourceExpression, Expression.Quote(lambda)
                     );

                    // same as before, but for hasPrevious
                    binaryExpression = Expression.MakeBinary(hasPreviousBinaryType, left, right);
                    lambda = Expression.Lambda(binaryExpression, parameter);

                    // lazy create the hasPreviousExpression
                    if (hasPreviousExpression == null)
                    {
                        hasPreviousExpression = node;
                    }

                    hasPreviousExpression = Expression.Call(
                         null,
                         Where,
                         hasPreviousExpression, Expression.Quote(lambda)
                    );
                }
            }

            // reverse the query, later on we need to revert this
            // since cursor pagination should not reverse the items order
            if (backwards)
            {
                sourceExpression = Expression.Call(
                    null,
                    Reverse,
                    sourceExpression
                );
            }

            // the expression, whitout Take
            Expression countExpression = sourceExpression;

            // we need to put back this call
            if (skip > 1 || skip < 0) // preserve the < 0
            {
                sourceExpression = Expression.Call(
                    null,
                    Skip,
                    sourceExpression, Expression.Constant(skip)
                );
            }

            // adjust take
            if (take.HasValue)
            {
                // always positive
                take = Math.Abs(take.Value);

                sourceExpression = Expression.Call(
                    null,
                    Take,
                    sourceExpression, Expression.Constant(take.Value + (peek ? 1 : 0)) // take 1 more
                );
            }

            countExpression = Expression.Call(
                null,
                Count,
                countExpression
            );

            // hasPreviousExpression is lazy
            hasPreviousExpression = hasPreviousExpression is null ? null : Expression.Call(
                null,
                Any,
                hasPreviousExpression
            );

            Dictionary<string, MemberInfo>? orderingMembers;
            if (orderByExpressions.Count > 0)
            {
                orderingMembers = new Dictionary<string, MemberInfo>();

                foreach (var kvp in orderByExpressions)
                {
                    // ow boys, what the heck is that?
                    var memberInfo = ((MemberExpression)((LambdaExpression)((UnaryExpression)kvp.Value.Arguments[1]).Operand).Body).Member;

                    orderingMembers.Add(kvp.Key, memberInfo);
                }
            }
            else
            {
                orderingMembers = null;
            }

            var cursorExpression = new CursorExpression<T>(countExpression, hasPreviousExpression, sourceExpression, backwards, take, peek, orderingMembers);

            return cursorExpression;
        }

        bool TryVisitOrderingExpression(
            string key,
            MethodCallExpression orderByCall,
            CursorProvider? cursorProvider,
            [NotNullWhen(returnValue: true)] out ParameterExpression? parameter,
            [NotNullWhen(returnValue: true)] out Expression? left,
            [NotNullWhen(returnValue: true)] out Expression? right
            )
        {
            // what we do here is trivial but finicky since we are dealing with Expressions
            // build the cursor expression from the cursorProvider and orderByCall
            // if no value is provided, use the TakeWile expression
            // return false and null if we do not find anything

            if (cursorProvider is null)
            {
                if (takeWhileExpressions.TryGetValue(key, out var takeWhile))
                {
                    var lb = (LambdaExpression)((UnaryExpression)takeWhile.Arguments[1]).Operand;
                    var bx = (BinaryExpression)lb.Body;

                    parameter = lb.Parameters[0];
                    left = bx.Left;
                    right = bx.Right;

                    return true;
                }
                else
                {
                    parameter = null;
                    left = null;
                    right = null;

                    return false;
                }
            }

            else
            {
                var lb = (LambdaExpression)((UnaryExpression)orderByCall.Arguments[1]).Operand;
                var mx = (MemberExpression)lb.Body;

                Type memberType;
                if (mx.Member is PropertyInfo propertyInfo) memberType = propertyInfo.PropertyType;
                else if (mx.Member is FieldInfo fieldInfo) memberType = fieldInfo.FieldType;

                else
                {
                    // this should never happen.
                    throw new InvalidOperationException($"Invalid Expression member, expected PropertyInfo or FieldInfo, got '{mx.Member.GetType().Name}'");
                }

                if (cursorProvider.TryGetValue(key, memberType, out var value))
                {
                    parameter = lb.Parameters[0];
                    left = mx;
                    right = Expression.Constant(value);
                    return true;
                }
                else
                {
                    if (takeWhileExpressions.TryGetValue(key, out var takeWhile))
                    {
                        lb = (LambdaExpression)((UnaryExpression)takeWhile.Arguments[1]).Operand;
                        var bx = (BinaryExpression)lb.Body;

                        parameter = lb.Parameters[0];
                        left = bx.Left;
                        right = bx.Right;
                        return true;
                    }
                    else
                    {
                        parameter = null;
                        left = null;
                        right = null;
                        return false;
                    }
                }
            }
        }

        /// <summary>
        ///     Create a new <see cref="CursorExpression{T}"/> from the query expression.
        /// </summary>
        /// <param name="node"> The expression to visit. </param>
        /// <param name="cursorProvider"> The cursor provider for expression. </param>
        /// <param name="peek"> True when it should peek past the end of the sequence to see if there are any more elements. </param>
        /// <returns>
        ///     The <see cref="CursorExpression{T}"/> representing the cursor operation for the expression.
        /// </returns>
        [return: NotNullIfNotNull("node")]
        public static CursorExpression<T>? Visit(Expression? node, CursorProvider? cursorProvider, bool peek)
        {
            // we need to do this for now...
            return new CursorExpressionVisitor<T>().VisitInternal(node, cursorProvider, peek);
        }
    }

    /// <summary>
    ///     Represents a cursor expression for <see cref="IQueryable{T}"/>.
    /// </summary>
    public sealed class CursorExpression<T> : Expression
    {
        /// <summary>
        ///     Gets the total count expression.
        /// </summary>
        public Expression TotalCount { get; }

        /// <summary>
        ///     Gets the has previous expression, if a cursor is applied.
        /// </summary>
        /// <remarks>
        ///     Without a cursor, there are no previous elements.
        /// </remarks>
        public Expression? HasPrevious { get; }

        /// <summary>
        ///     Gets a value indicating if take was initially negative.
        /// </summary>
        public bool Backwards { get; }

        /// <summary>
        ///     Gets the maximum number of contiguous elements to retrive from the source.
        ///     <see langword="null"/> indicates that no such limit exists.
        /// </summary>
        /// <remarks>
        ///     This value will always be positive, use <see cref="Backwards"/> to check if this value was initially negative.
        /// </remarks>
        public int? Take { get; }

        /// <summary>
        ///     Indicates if <see cref="Take"/> has been artificially incremented by 1
        ///     with the intention to peek past the end of the sequence to check if there are more elements available.
        /// </summary>
        /// <remarks>
        ///     Can be true even when <see cref="Take"/> is <see langword="null"/>.
        /// </remarks>
        public bool Peek { get; }

        /// <inheritdoc />
        public override bool CanReduce => _connectionExpression.CanReduce;

        /// <inheritdoc />
        public override ExpressionType NodeType => _connectionExpression.NodeType;

        /// <inheritdoc />
        public override Type Type => _connectionExpression.Type;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly Expression _connectionExpression;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly IReadOnlyDictionary<string, MemberInfo>? _orderingMembers;

        internal CursorExpression(
            Expression countExpression,
            Expression? hasPreviousExpression,
            Expression connectionExpression,
            bool backwards,
            int? take,
            bool peek,
            IReadOnlyDictionary<string, MemberInfo>? orderingMembers)
        {
            TotalCount = countExpression;
            HasPrevious = hasPreviousExpression;
            _connectionExpression = connectionExpression;
            Backwards = backwards;
            Take = take;
            Peek = peek;
            _orderingMembers = orderingMembers;
        }

        /// <inheritdoc />
        public override Expression Reduce()
            => _connectionExpression.Reduce();

        /// <inheritdoc />
        protected override Expression Accept(ExpressionVisitor visitor)
            => VisitChildren(visitor);

        /// <inheritdoc />
        protected override Expression VisitChildren(ExpressionVisitor visitor)
            => visitor.Visit(_connectionExpression.CanReduce ? _connectionExpression.ReduceAndCheck() : _connectionExpression);

        /// <inheritdoc />
        public override string ToString() => _connectionExpression.ToString();

        /// <summary>
        ///     Creates a cursor for the specified node, or <see langword="null"/> if the node is <see langword="null"/>
        ///     or there are no OrderBy clauses in the expression.
        /// </summary>
        /// <param name="node"> The object from which create the cursor from. </param>
        /// <returns>
        ///     The cursor of the specified node, if any.
        /// </returns>
        public ICursor? GetCursor(T node)
        {
            // null are ok, they just produce null cursors
            // it is not possible to create a cursor whitout ordering ordering
            if (node is null || _orderingMembers is null) return null;

            Debug.Assert(_orderingMembers.Count > 0);

            var cursorKeys = new Dictionary<string, ICursorKey>();
            foreach (var kvp in _orderingMembers)
            {
                var vaue = GetValue(kvp.Value, node, out var valueType);

                cursorKeys.Add(kvp.Key, new CursorKey(kvp.Key, vaue, valueType));
            }

            return new Cursor(cursorKeys);
        }

        /// <summary> Returns the member value and type of a specified object. </summary>
        private static object? GetValue(MemberInfo memberInfo, object obj, out Type valueType)
        {
            if (memberInfo is PropertyInfo propertyInfo)
            {
                valueType = propertyInfo.PropertyType;
                return propertyInfo.GetValue(obj, null);
            }

            if (memberInfo is FieldInfo fieldInfo)
            {
                valueType = fieldInfo.FieldType;
                return fieldInfo.GetValue(obj);
            }

            // this should never happen
            throw new InvalidOperationException($"Cannot GetValue.");
        }
    }

    /// <summary>
    ///     Represents a cursor provider for a cursor query.
    /// </summary>
    public abstract class CursorProvider
    {
        /// <summary>
        ///     Initializes a new instance of <see cref="CursorProvider"/>.
        /// </summary>
        protected CursorProvider() { }

        /// <summary>
        ///     Get a value for the specified key, with the specified type.
        /// </summary>
        /// <remarks>
        ///     A value with a different type could lead to unexpected results.
        /// </remarks>
        /// <param name="key"> The cursor key name. </param>
        /// <param name="type"> The cursor type. </param>
        /// <param name="value"> The cursor value. </param>
        /// <returns>
        ///     True if a cursor value has been provided, false otherwise.
        /// </returns>
        protected internal virtual bool TryGetValue(string key, Type type, out object? value)
        {
            value = default;
            return false;
        }
    }
}
