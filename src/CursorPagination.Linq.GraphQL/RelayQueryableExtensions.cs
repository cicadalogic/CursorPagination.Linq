using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GraphQL.Builders;
using GraphQL.Language.AST;
using GraphQL.Types.Relay.DataObjects;

namespace System.Linq.CursorPagination
{
    using System.Linq;

    /// <summary>
    ///     Extends the Linq cursor to support <see cref="GraphQL.Types.Relay.DataObjects.Connection{TNode}"/>.
    /// </summary>
    public static class RelayQueryableExtensions
    {
        private static IQueryable<TSource> PrepareSource<TSource>(
            IQueryable<TSource> source,
            IResolveConnectionContext context,
            int? maxPageSize,
            CursorSerializer cursorSerializer,
            out bool withTotalCount)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.PageSize.HasValue && context.PageSize <= 0) throw new ArgumentOutOfRangeException(nameof(context), context.PageSize.Value, $"{nameof(context)}.PageSize must be positive or null.");
            if (maxPageSize.HasValue && maxPageSize <= 0) throw new ArgumentOutOfRangeException(nameof(maxPageSize), maxPageSize.Value, $"{maxPageSize} must be positive or null.");

            // when both After and Before are presents, use After
            //if (context.After is not null && context.Before is not null)
            //{
            //    throw new InvalidOperationException("Bidirectional pagination is not supported, do not use After and Before together.");
            //}

            // uncomment this if you want to reject negative or zero values
            //if (context.First.HasValue && context.First <= 0) throw new ArgumentOutOfRangeException(nameof(context), context.First.Value, $"first must be positive or null.");
            //if (context.Last.HasValue && context.Last <= 0) throw new ArgumentOutOfRangeException(nameof(context), context.Last.Value, $"first must be positive or null.");

            // use PageSize on null values
            var first = context.First ?? context.PageSize;
            var last = context.Last ?? context.PageSize;

            // ignore the negative or zero values (as many pagination library does)
            // NOTE: you need to remove this code if you want to allow nevative or zero values
            if (first.HasValue && first <= 0) first = context.PageSize ?? maxPageSize;
            if (last.HasValue && last <= 0) last = context.PageSize ?? maxPageSize;

            // if we have a maxPageSize, coerce the value
            if (maxPageSize.HasValue)
            {
                if (!first.HasValue || (first > maxPageSize)) first = maxPageSize;
                if (!last.HasValue || (last > maxPageSize)) last = maxPageSize;
            }

            // now first and last are null || (> 0 && <= maxPageSize)

            // TODO: this is not enough, with Total Count need to be parametric
            // check if we need to include the total count
            withTotalCount = context.FieldAst.SelectionSet?.Children.Any(p => p is Field field && field.Name == "totalCount") ?? false;

            // alaways skip 1
            source = source.Skip(1);

            // Take
            if (context.After != null)
            {
                if (context.First.HasValue) source = source.Take(context.First.Value);

                cursorSerializer.SetCursor(context.After);
            }
            else if (context.Before != null)
            {
                if (context.Last.HasValue) source = source.Take(-1 * context.Last.Value);

                cursorSerializer.SetCursor(context.Before);
            }
            else
            {
                if (context.First.HasValue) source = source.Take(context.First.Value);
                else if (context.Last.HasValue) source = source.Take(-1 * context.Last.Value);
            }

            return source;
        }

        /// <summary>
        ///     Creates a <see cref="GraphQL.Types.Relay.DataObjects.Connection{TSource}" /> from an <see cref="IQueryable{T}" />.
        /// </summary>
        public static Connection<TSource> ToGraphQLConnection<TSource>(
            this IQueryable<TSource> source,
            IResolveConnectionContext context,
            int? maxPageSize = null,
            CursorSerializer? cursorSerializer = null)
        {
            // create a default cursor serializer
            cursorSerializer ??= new CursorSerializer();

            source = PrepareSource(source, context, maxPageSize, cursorSerializer, out var withTotalCount);

            var cursorExpression = CursorExpressionVisitor<TSource>.Visit(source.Expression, cursorSerializer, true);

            int? totalCount = withTotalCount ? source.Provider.Execute<int>(cursorExpression.TotalCount) : null;
            bool hasPrevious = cursorExpression.HasPrevious is not null && source.Provider.Execute<bool>(cursorExpression.HasPrevious);

            List<Edge<TSource>> edges = new();
            foreach (TSource node in source.Provider.CreateQuery<TSource>(cursorExpression))
            {
                ICursor? cursor = cursorExpression.GetCursor(node);

                edges.Add(new Edge<TSource>
                {
                    Cursor = cursor == null ? null : cursorSerializer.Serialize(cursor),
                    Node = node
                });
            }

            return CreateConnection(edges, cursorExpression.Take, cursorExpression.Backwards, totalCount, hasPrevious);
        }

        /// <summary>
        ///     Creates a <see cref="GraphQL.Types.Relay.DataObjects.Connection{TSource}" /> from an <see cref="IQueryable{T}" />.
        /// </summary>
        public static Connection<TSource> ToGraphQLConnection<TSource>(
            this IQueryable<TSource> source,
            IResolveConnectionContext context,
            int? maxPageSize,
            CursorSerializer? cursorSerializer,
            bool? withTotalCount)
        {
            // create a default cursor serializer
            cursorSerializer ??= new CursorSerializer();

            source = PrepareSource(source, context, maxPageSize, cursorSerializer, out var computeTotalCount);

            var cursorExpression = CursorExpressionVisitor<TSource>.Visit(source.Expression, cursorSerializer, true);

            bool forceTotalCount;
            if (withTotalCount.HasValue) forceTotalCount = withTotalCount.Value;
            else forceTotalCount = computeTotalCount;

            int? totalCount = forceTotalCount ? source.Provider.Execute<int>(cursorExpression.TotalCount) : null;
            bool hasPrevious = cursorExpression.HasPrevious is not null && source.Provider.Execute<bool>(cursorExpression.HasPrevious);

            List<Edge<TSource>> edges = new();
            foreach (TSource node in source.Provider.CreateQuery<TSource>(cursorExpression))
            {
                ICursor? cursor = cursorExpression.GetCursor(node);

                edges.Add(new Edge<TSource>
                {
                    Cursor = cursor == null ? null : cursorSerializer.Serialize(cursor),
                    Node = node
                });
            }

            return CreateConnection(edges, cursorExpression.Take, cursorExpression.Backwards, totalCount, hasPrevious);
        }

        /// <summary>
        ///     Creates a <see cref="GraphQL.Types.Relay.DataObjects.Connection{TSource}" /> from an <see cref="IQueryable{T}" /> by enumerating it
        ///     asynchronously.
        /// </summary>
        public static async Task<Connection<TSource>> ToGraphQLConnectionAsync<TSource>(
            this IQueryable<TSource> source,
            IResolveConnectionContext context,
            int? maxPageSize = null,
            CursorSerializer? cursorSerializer = null,
            CancellationToken cancellationToken = default)
        {
            // create a default cursor serializer
            cursorSerializer ??= new CursorSerializer();

            source = PrepareSource(source, context, maxPageSize, cursorSerializer, out var withTotalCount);

            var cursorExpression = CursorExpressionVisitor<TSource>.Visit(source.Expression, cursorSerializer, true);

            if (source.Provider.CreateQuery<TSource>(cursorExpression) is not IAsyncEnumerable<TSource> asyncEnumerable)
            {
                throw new InvalidOperationException($"The source 'IQueryable' doesn't implement 'IAsyncEnumerable<{typeof(TSource)}>'.");
            }

            int? totalCount;
            bool hasPrevious;

            if (source.Provider is Microsoft.EntityFrameworkCore.Query.IAsyncQueryProvider provider)
            {
                totalCount = withTotalCount ? await provider.ExecuteAsync<Task<int>>(cursorExpression.TotalCount, cancellationToken) : null;
                hasPrevious = cursorExpression.HasPrevious is not null && await provider.ExecuteAsync<Task<bool>>(cursorExpression.HasPrevious, cancellationToken);
            }
            else
            {
                totalCount = withTotalCount ? source.Provider.Execute<int>(cursorExpression.TotalCount) : null;
                hasPrevious = cursorExpression.HasPrevious is not null && source.Provider.Execute<bool>(cursorExpression.HasPrevious);
            }

            List<Edge<TSource>> edges = new();
            await foreach (TSource node in asyncEnumerable.WithCancellation(cancellationToken))
            {
                ICursor? cursor = cursorExpression.GetCursor(node);

                edges.Add(new Edge<TSource>
                {
                    Cursor = cursor == null ? null : cursorSerializer.Serialize(cursor),
                    Node = node
                });
            }

            return CreateConnection(edges, cursorExpression.Take, cursorExpression.Backwards, totalCount, hasPrevious);
        }

        /// <summary>
        ///     Creates a <see cref="GraphQL.Types.Relay.DataObjects.Connection{TSource}" /> from an <see cref="IQueryable{T}" /> by enumerating it
        ///     asynchronously.
        /// </summary>
        public static async Task<Connection<TSource>> ToGraphQLConnectionAsync<TSource>(
            this IQueryable<TSource> source,
            IResolveConnectionContext context,
            int? maxPageSize,
            CursorSerializer? cursorSerializer,
            bool? withTotalCount,
            CancellationToken cancellationToken = default)
        {
            // create a default cursor serializer
            cursorSerializer ??= new CursorSerializer();

            source = PrepareSource(source, context, maxPageSize, cursorSerializer, out var computeTotalCount);

            var cursorExpression = CursorExpressionVisitor<TSource>.Visit(source.Expression, cursorSerializer, true);

            if (source.Provider.CreateQuery<TSource>(cursorExpression) is not IAsyncEnumerable<TSource> asyncEnumerable)
            {
                throw new InvalidOperationException($"The source 'IQueryable' doesn't implement 'IAsyncEnumerable<{typeof(TSource)}>'.");
            }

            bool forceTotalCount;
            if (withTotalCount.HasValue) forceTotalCount = withTotalCount.Value;
            else forceTotalCount = computeTotalCount;

            int? totalCount;
            bool hasPrevious;

            if (source.Provider is Microsoft.EntityFrameworkCore.Query.IAsyncQueryProvider provider)
            {
                totalCount = forceTotalCount ? await provider.ExecuteAsync<Task<int>>(cursorExpression.TotalCount, cancellationToken) : null;
                hasPrevious = cursorExpression.HasPrevious is not null && await provider.ExecuteAsync<Task<bool>>(cursorExpression.HasPrevious, cancellationToken);
            }
            else
            {
                totalCount = forceTotalCount ? source.Provider.Execute<int>(cursorExpression.TotalCount) : null;
                hasPrevious = cursorExpression.HasPrevious is not null && source.Provider.Execute<bool>(cursorExpression.HasPrevious);
            }

            List<Edge<TSource>> edges = new();
            await foreach (TSource node in asyncEnumerable.WithCancellation(cancellationToken))
            {
                ICursor? cursor = cursorExpression.GetCursor(node);

                edges.Add(new Edge<TSource>
                {
                    Cursor = cursor == null ? null : cursorSerializer.Serialize(cursor),
                    Node = node
                });
            }

            return CreateConnection(edges, cursorExpression.Take, cursorExpression.Backwards, totalCount, hasPrevious);
        }

        /// <summary>
        ///     Creates a <see cref="GraphQL.Types.Relay.DataObjects.Connection{TNode}" /> from an <see cref="IConnection{T}" />.
        /// </summary>
        public static Connection<TNode> ToGraphQLConnection<TNode>(this IConnection<TNode> connection, CursorSerializer cursorSerializer)
        {
            if (connection is null) throw new ArgumentNullException(nameof(connection));
            if (cursorSerializer is null) throw new ArgumentNullException(nameof(cursorSerializer));

            List<Edge<TNode>>? edges;
            if (connection.Edges is null) // sanity check
            {
                edges = null;
            }
            else
            {
                // create the edge list
                edges = connection.Edges
                    .Select(edge => new Edge<TNode>
                    {
                        Node = edge.Node,
                        Cursor = edge.Cursor is null ? null : cursorSerializer.Serialize(edge.Cursor)
                    })
                    .ToList();
            }

            PageInfo? pageInfo;
            if (connection.PageInfo is null) // sanity check
            {
                pageInfo = null;
            }
            else
            {
                // create the PageInfo
                pageInfo = new PageInfo()
                {
                    HasNextPage = connection.PageInfo.HasNextPage,
                    HasPreviousPage = connection.PageInfo.HasPreviousPage,
                    StartCursor = edges?.FirstOrDefault()?.Cursor, // do not compute the cursor again
                    EndCursor = edges?.LastOrDefault()?.Cursor,
                };
            }

            // we use checked to throw, by now with this implementation it is not possible to overflow
            int? totalCount = connection.TotalCount.HasValue ? checked((int)connection.TotalCount.Value) : null;

            return new Connection<TNode>()
            {
                TotalCount = totalCount,
                PageInfo = pageInfo,
                Edges = edges,
            };
        }

        private static Connection<T> CreateConnection<T>(List<Edge<T>> edges, int? take, bool backwards, int? totalCount, bool hasPrevious)
        {
            bool hasNext;

            // assume Peek is true
            if (take.HasValue)
            {
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

            // create the PageInfo
            var pageInfo = new PageInfo()
            {
                HasNextPage = hasNext,
                HasPreviousPage = hasPrevious,
                StartCursor = edges?.FirstOrDefault()?.Cursor, // do not compute the cursor again
                EndCursor = edges?.LastOrDefault()?.Cursor,
            };

            return new Connection<T>()
            {
                TotalCount = totalCount,
                PageInfo = pageInfo,
                Edges = edges,
            };
        }
    }
}
