using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace System.Linq.CursorPagination
{
    /// <summary>
    ///     Represents a cursor part.
    /// </summary>
    public interface ICursorKey
    {
        /// <summary>
        ///     The cursor key.
        /// </summary>
        string Key { get; }

        /// <summary>
        ///     The cursor value.
        /// </summary>
        object? Value { get; }

        /// <summary>
        ///     The cursor type.
        /// </summary>
        Type Type { get; }
    }

    /// <summary>
    ///     Represents a cursor.
    /// </summary>
    public interface ICursor : IReadOnlyDictionary<string, ICursorKey>
    {
    }

    /// <summary>
    ///     Contains pagination information relating to the result data set.
    /// </summary>
    public interface IPageInfo
    {
        /// <summary>
        ///     Indicates if there are additional pages of data that can be returned.
        /// </summary>
        public bool HasNextPage { get; }

        /// <summary>
        ///     Indicates if there are prior pages of data that can be returned.
        /// </summary>
        public bool HasPreviousPage { get; }

        /// <summary>
        ///     The cursor of the first node in the result data set.
        /// </summary>
        public ICursor? StartCursor { get; }

        /// <summary>
        ///     The cursor of the last node in the result data set.
        /// </summary>
        public ICursor? EndCursor { get; }
    }

    /// <summary>
    ///     Represents an edge of a connection containing a node (a row of data) and cursor (a unique identifier for the row of data).
    /// </summary>
    /// <typeparam name="TNode">The data type.</typeparam>
    public interface IEdge<out TNode>
    {
        /// <summary>
        ///     The cursor of this edge's node. A cursor is a string representation of a unique identifier of this node.
        /// </summary>
        public ICursor? Cursor { get; }

        /// <summary>
        ///     The node. A node is a single row of data within the result data set.
        /// </summary>
        public TNode? Node { get; }
    }

    /// <summary>
    ///     Represents a connection result containing nodes and pagination information, with an edge type of <see cref="IEdge{TNode}"/>.
    /// </summary>
    /// <typeparam name="TNode">The data type.</typeparam>
    public interface IConnection<out TNode>
    {
        /// <summary>
        /// The total number of records available. Returns <see langword="null"/> if the total number is unknown.
        /// </summary>
        public long? TotalCount { get; }

        /// <summary>
        /// Additional pagination information for this result data set.
        /// </summary>
        public IPageInfo PageInfo { get; }

        /// <summary>
        /// The result data set, stored as a list of edges containing a node (the data) and a cursor (a unique identifier for the data).
        /// </summary>
        public IReadOnlyList<IEdge<TNode>> Edges { get; }
    }

    /// <summary>
    ///     Provides a default <see cref="ICursorKey"/> implementation.
    /// </summary>
    [DebuggerDisplay("{Key,nq}={Value}")]
    internal sealed class CursorKey : ICursorKey
    {
        /// <inheritdoc />
        public string Key { get; }

        /// <inheritdoc />
        public object? Value { get; }

        /// <inheritdoc />
        public Type Type { get; }

        /// <summary>
        ///     Creates a new <see cref="CursorKey"/>.
        /// </summary>
        /// <param name="key"> The cursor key. </param>
        /// <param name="value"> The cursor value. </param>
        /// <param name="type"> The cursor type. </param>
        public CursorKey(string key, object? value, Type type)
        {
            if (key is null) throw new ArgumentNullException(nameof(key));
            if (key.Length == 0) throw new ArgumentException($"{nameof(key)} is empty.", nameof(key));

            Key = key;
            Value = value;
            Type = type ?? throw new ArgumentNullException(nameof(type));
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{Key}={Value}";
        }
    }

    /// <summary>
    ///     Provides a default <see cref="ICursor"/> implementation.
    /// </summary>
    [DebuggerDisplay("{ToString()}")]
    internal sealed class Cursor : ICursor
    {
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly ImmutableDictionary<string, ICursorKey> _dictionary;

        /// <inheritdoc />
        public int Count => _dictionary.Count;

        /// <inheritdoc />
        public IEnumerable<string> Keys => _dictionary.Keys;

        /// <inheritdoc />
        public IEnumerable<ICursorKey> Values => _dictionary.Values;

        /// <inheritdoc />
        public ICursorKey this[string key] => _dictionary[key];

        /// <summary>
        ///     Creates a new <see cref="ICursor"/> from the specified keys.
        /// </summary>
        /// <param name="cursorKeys"> The cursor keys. </param>
        /// <exception cref="ArgumentNullException"> <paramref name="cursorKeys"/> is null. </exception>
        /// <exception cref="ArgumentException"> <paramref name="cursorKeys"/> is empty. </exception>
        public Cursor(IEnumerable<ICursorKey> cursorKeys)
        {
            if (cursorKeys == null) throw new ArgumentNullException(nameof(cursorKeys));

            var immutableDictionary = cursorKeys.ToImmutableDictionary(p => p.Key);
            if (immutableDictionary.Count == 0)
            {
                throw new ArgumentException($"{nameof(cursorKeys)} is empty.", nameof(cursorKeys));
            }

            _dictionary = immutableDictionary;
        }

        /// <summary>
        ///     Creates a new <see cref="ICursor"/> from the specified keys.
        /// </summary>
        /// <param name="cursorKeys"> The cursor keys. </param>
        /// <exception cref="ArgumentNullException"> <paramref name="cursorKeys"/> is null. </exception>
        /// <exception cref="ArgumentException"> <paramref name="cursorKeys"/> is empty. </exception>
        public Cursor(IReadOnlyDictionary<string, ICursorKey> cursorKeys)
        {
            if (cursorKeys == null) throw new ArgumentNullException(nameof(cursorKeys));
            if (cursorKeys.Count == 0)
            {
                throw new ArgumentException($"{nameof(cursorKeys)} is empty.", nameof(cursorKeys));
            }

            _dictionary = cursorKeys.ToImmutableDictionary();
        }

        /// <inheritdoc />
        public override string ToString()
        {
            // this is safe, we almost always deal with value types for cursors
            return string.Join('&', this);
        }

        /// <inheritdoc />
        public bool ContainsKey(string key) => _dictionary.ContainsKey(key);

        /// <inheritdoc />
        public bool TryGetValue(string key, [MaybeNullWhen(false)] out ICursorKey value) => _dictionary.TryGetValue(key, out value);

        /// <inheritdoc />
        public IEnumerator<KeyValuePair<string, ICursorKey>> GetEnumerator() => _dictionary.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_dictionary).GetEnumerator();
    }

    /// <summary>
    ///     Provides a default <see cref="IPageInfo"/> implementation.
    /// </summary>
    [DebuggerDisplay("HasNextPage = {HasNextPage}, HasPreviousPage = {HasPreviousPage}, StartCursor = {StartCursor}, EndCursor = {EndCursor}")]
    internal sealed class PageInfo : IPageInfo
    {
        /// <inheritdoc />
        public bool HasNextPage { get; }

        /// <inheritdoc />
        public bool HasPreviousPage { get; }

        /// <inheritdoc />
        public ICursor? StartCursor { get; }

        /// <inheritdoc />
        public ICursor? EndCursor { get; }

        /// <summary>
        ///     Creates a new <see cref="PageInfo"/>.
        /// </summary>
        /// <param name="hasNextPage"> The next page, if present. </param>
        /// <param name="hasPreviousPage"> The previous, if present. </param>
        /// <param name="startCursor"> The start cursor. </param>
        /// <param name="endCursor"> The end cursor. </param>
        public PageInfo(bool hasNextPage, bool hasPreviousPage, ICursor? startCursor, ICursor? endCursor)
        {
            HasNextPage = hasNextPage;
            HasPreviousPage = hasPreviousPage;
            StartCursor = startCursor;
            EndCursor = endCursor;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"HasNextPage={HasNextPage}, HasPreviousPage={HasPreviousPage}, StartCursor={StartCursor}, EndCursor={EndCursor}";
        }
    }

    /// <summary>
    ///     Provides a default <see cref="IEdge{TNode}"/> implementation.
    /// </summary>
    [DebuggerDisplay("{Node}, Cursor = {Cursor}")]
    internal sealed class Edge<TNode> : IEdge<TNode>
    {
        /// <inheritdoc />
        public ICursor? Cursor { get; }

        /// <inheritdoc />
        public TNode? Node { get; }

        /// <summary>
        ///     Creates a new <see cref="Edge{TNode}"/>.
        /// </summary>
        /// <param name="cursor"> The edge cursor. </param>
        /// <param name="node"> The edge node. </param>
        public Edge(ICursor? cursor, TNode? node)
        {
            Cursor = cursor;
            Node = node;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{Node}, Cursor={Cursor}";
        }
    }

    /// <summary>
    ///     Provides a default <see cref="IConnection{TNode}"/> implementation.
    /// </summary>
    [DebuggerDisplay("Count = {Edges.Count}, TotalCount = {TotalCount}")]
    internal sealed class Connection<TNode> : IConnection<TNode>
    {
        /// <inheritdoc />
        public long? TotalCount { get; }

        /// <inheritdoc />
        public IPageInfo PageInfo { get; }

        /// <inheritdoc />
        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public IReadOnlyList<IEdge<TNode>> Edges { get; }

        /// <summary>
        ///     Creates a new <see cref="Connection{TNode}"/>.
        /// </summary>
        /// <param name="totalCount"> The total count connection. </param>
        /// <param name="pageInfo"> The connection page info. </param>
        /// <param name="edges"> The connection edges. </param>
        /// <exception cref="ArgumentNullException"> pageInfo is null. </exception>
        /// <exception cref="ArgumentNullException"> edges is null. </exception>
        public Connection(int? totalCount, IPageInfo pageInfo, IReadOnlyList<IEdge<TNode>> edges)
        {
            TotalCount = totalCount;
            PageInfo = pageInfo ?? throw new ArgumentNullException(nameof(pageInfo));
            Edges = edges ?? throw new ArgumentNullException(nameof(edges));
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"Count={Edges.Count}, TotalCount={TotalCount}";
        }
    }
}
