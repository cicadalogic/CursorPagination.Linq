# Cursor Pagination for LINQ

This library extend LINQ to support cursor pagination.

```ToConnection``` will create and execute a relay style connection from a query:

```csharp
using System.Linq.CursorPagination;

// a cursor pointing at (Id, 100), (CreatedAt, date), it can be another Post instance
var cursor = new { Id = 100, CreatedAt = date };

var q = db.Posts
    // set the order
    .OrderBy(x => x.Id)
    .OrderBy(x => x.CreatedAt)

    // set the cursor on Id and CreatedAt
    .TakeWhile(x => x.Id > cursor.Id)
    .TakeWhile(x => x.Id > cursor.CreatedAt)

    // do not include the cursor in the result
    .Skip(1)

    // take 10 elements
    .Take(10);

// execute the query, including a total count
IConnection<Post> cnn = await q.ToConnectionAsync(withTotalCount: true);

// the result will be:
public interface IConnection<Post>
{
    public long? TotalCount { get; }
    public IPageInfo PageInfo { get; }
    public ICollection<IEdge<Post>> Edges { get; }
}

public interface IEdge<out Post>
{
    public ICursor? Cursor { get; }
    public Post? Node { get; }
}

public interface IPageInfo
{
    public bool HasNextPage { get; }
    public bool HasPreviousPage { get; }
    public ICursor? StartCursor { get; }
    public ICursor? EndCursor { get; }
}
```

## Documentation

> This library does almost nothing, however what it does is really finicky, as such, it will not align perfectly to what LINQ and EF core does, especially when you use a query that relies on side effects, such as using Take before Skip.

In the documentation, we are going to refer to the following data as an example:

```csharp
class ApplicationDbContext : DbContext
{
    public DbSet<Post> Posts { get; }
}

class Post
{
    public int Id { get; }
    public DateTime CreatedAt { get; }
}

ApplicationDbContext db;
```

## Setting a cursor

The library provide two way to set a cursor.

Using ```TakeWhile``` or using a ```CursorProvider```, both solves the same problem in two different ways.

### TakeWile

Will allow a cursor to be set directly in the query by using typical LINQ, ignore the use of the ```<``` operator, we discuss this in more details later.

```csharp
db.Posts.
    // set a cursor on the Id, with a value of 10
    .TakeWhile(x => x.Id > 10)

     // sort the Id, or the cursor will not be applied
    .OrderBy(x => x.Id)
```

Each column in which we want to set a cursor, must have an ordering clause, trying to set a cursor for an unordered column, will do nothing.

The operator inside a ```TakeWhile``` call is computed accordingly with the direction of the query, meaning when constructing the query, the operator will have no effects. So from a point of view strictly technical, you are free to use any logical operator, however there are some more considerations to be done.

When setting a cursor, it is important to not being confused by the ordering and the logical operators, since the operator do not matter, the confusion could be even greater between developers.

When deciding what operator to use, when you are ordering descending, use the operator ```<``` to indicate the default ordering is descending and viceversa.

```csharp
    // sort ascending, so we use >
    .OrderBy(x => x.Id).TakeWhile(x => x.Id > 10)

    // sort descending, so we use <
    .OrderByDescending(x => x.Id).TakeWhile(x => x.Id < 10)
```

## CursorProvider

When setting the cursor programmatically, you are likely setting the ordering, you may want to go backwards, there are a bunch of ```if``` scattered around, you also need to have a way to call the appropriate expression ```q.TakeWhile(x => x.Id > 10)```, you need a way to actually store and retrive cursors in the form of a string, or a serialized class and so on.

```TakeWhile()``` requires you to deal with cursor objects of the same type of the query, which does not play well when you only care about set a column and a value.

An example of setting a cursor using an object:

```csharp
// create an extension
static IQueryable<Post> SetCursor<T>(this IQueryable<Post> source, Post cursor)
{
    return source.
        TakeWhile(x => x.Id > cursor.Id)
        TakeWhile(x => x.CreatedAt > cursor.CreatedAt);
}

var cusros = new Post() {
    Id = 10,
    CreatedAt = Yesterday,
};

// now you can do:
db.Posts
    .OrderBy(x => x.Id)
    .SetCursor(cursor) // CreatedAt will not be used, there is no OrderBy(CreatedAt)
    .ToConnection();

```

This approach is sounds and works really well, you can image using a specialized class that can be easily serialized and deserialized, and used as a cursor for a given type.

However, its is hard to generalize without going in the realm of reflection and expressions, when the only things that really matter is a property name and a value of the appropriate type.

To avoid dealing with expressions and gigantic switch cases, you can set a cursor using a ```CursorProvider```.

When the ```CursorExpressionVisitor``` looks for a cursor, it will ask the provider for it.

If the provider do not have a value, the visitor will use the value from a corresponding ```TakeWhile()``` expression.

An example implementation that takes a json serialized cursor:

```csharp
var posts = db.Posts
    .OrderBy(x => x.Id) // x.Id is typeof(int)

    // use a cursor provider from a json dictionary
    .ToConnection(cursorProvider: new JsonCursorProvider("{\"Id\": \"10\"}"));

class JsonCursorProvider : CursorProvider
{
    public JsonCursorProvider(string json)
    {
        _keyStringValuePair = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
    }

    // will now contains ("Id", "10")
    private readonly Dictionary<string, object?> _keyStringValuePair;

    // this function will be called as: ("Id", typeof(int), out value);
    protected override bool TryGetValue(string key, Type type, out object? value)
    {
        // key is "Id", from OrderBy
        // type is typeof(int), Post.Id is int

        // no cursor, skip
        if (! keyStringValuePair.TryGetValue(key, out value)) {
            return false;
        }

        // "10" is a string, we need the appropriate type
        value = int.Parse(value);

        // we can also do this or similars:
        // value = Convert.ChangeType(value, type);

        return true;
    }
}
```

> This approach has a limitation, it is not possible to know in advanced the list of order by clauses unless you keep track of them, or iterate through the query expression.

## Caveat

There are some caveats, they are better shown with this code:

```csharp
    db.Posts
    .OrderBy(x => x.Id)

    .TakeWhile(x => x.Id < 10)
    .TakeWhile(x => x.CreatedAt < Yesterday) // will not work

    .ToConnection();

    db.Posts
    .TakeWhile(x => x.Id < 10) // will be removed no matter what
    ... // do stuff
    .OrderBy(x => x.Id)

    // this will not produce the expected result of Take a subquery
    // the calling order of Skip, Take do not matter
    .Take(10),
    .Skip(20),

    // skip and take will be removed, this call will end up after OrderBy
    .Select(...)

    // now this skip will be used and the above Skip(20) will be preserved
    // and the Select(...) call above will be after Skip(20)
    .Skip(10)

    // this will be removed and do nothing, CreatedAt is not sorted
    .TakeWhile(x => x.CreatedAt < Yesterday)

    .ToConnection();

    // the query will be rewritten as:
    // db.Posts
    // ... // do stuff
    // .OrderBy(x => x.Id)
    // .Skip(20),
    // .Select(...)
    // .Where(x => x.Id >= 10) <- this is the cursor
    // .Skip(10) <- skipping in a cursor will cause all sort of problems
    // .Take(10)

```

It is not possible to set a cursor for a column that has not been sorted explicitly, this is not a limitation of the library, but of the SQL specification itself.

Extract from SQL 92, General Rules, 4:

```text
If an <order by clause> is not specified, then the ordering of the rows of Q is implementation-dependent.
```

Infact, a cursor it is nothing more than a  ```WHERE``` clause, with its relational operator chosen in such a way to allow the query to be executed in the specified direction without excluding the set that we are taking.

This also implies that, setting the cursor operator whitout having the order, is not possible.

### Using AsConnection

```query.AsConnection()``` will rewrite the query translating all the ```TakeWhile``` calls into cursor.

> It will also remove any ```Skip()``` and ```Take()``` calls, sort then reinsert them back into the query, so it might break some type of query.

```csharp
db.Posts. <...>  .AsConnection()  .ToDictionaryAsync(...);
```

### Using ToNodeList

```.ToNodeList()```, similar to ```ToList```, but will preseve the order of the set.

If you call ```.AsConnection().ToList()``` having ```Take(-10)```, the result will be upside down. It might not be what you expected, for that you can use ```.ToNodeList()```, which will reverse the result when paginating backwards.

### Using a cursor with plain LINQ

It is actually possible to use a cursor pagination with LINQ without any external library whatsoever, even though it's cumbersome and totally impractical.

```csharp
    // this is a trivial cursor pagination, where 10 is our cursor
    db.Posts.OrderBy(x => x.Id).Where(x => x.Id >= 10).Take(20).ToList();

    // this is just a regular query, will not work as cursor connection
    db.Posts.OrderBy(x => x.Id).Where(x => x.Id < 10).ToList();

    // we need to do this instead
    db.Posts.OrderByDescending(x => x.Id).Where(x => x.Id < 10).ToList().Reverse();
```

## Paginating Backwards

> For now, the use of ```Reverse()``` is bogus, use ```Take()``` with a negative value or ```int.MinValue``` instead.

To paginate backwards, use a negative value when using ```Take```:

```csharp
Take(-10);
```

Alternatively, you can use ```TakeLast```

```csharp
TakeLast(10);

// its equivalent to:
Take(-10);
```

Not working right now:

~~You can also use ```Reverse()``` and keep using a positive value for take.~~

~~Also you may need to use ```Reverse()``` when you want to get the whole result set.~~

## Skip

Sometimes you do not need to include the cursor in the result set.

To achieve this, simply call:

```csharp
Skip(1);
```

If you are ordeding by descending ```id```, and the cursor is ```10```, the result set would look like:

| Id  |
|-----|
| 10  |
| 9   |
| 8   |
| 7   |
| ... |

However, if you ```Skip(1)```, the result set would be:

| Id  |
|-----|
| 9   |
| 8   |
| 7   |
| ... |

Note that no ```OFFSET``` will be used, a non inclusive logical operation (```"id" < 10``` or ```"id" > 10```) will be used instead.

Using ```Skip(0)``` will have no effects being the default behavior.

Note that you can still use ```Skip``` with a value greater than 1, however you are likely defeating the purpose of a cursor pagination by doing so.

## TotalCount

It is trivial to compute the expression for the total count, do not apply a limit to the query and ```SELECT COUNT```.

However, actually running the count could be expensive, so you need to manually specify when you want the total count to be included in the connection result.

## Next and Previous pages

When using ```ToConnection```, next and previous will always be evaluated.

Next is really cheap to do, it is done by incrementing take by 1, if the result set is take + 1, we have a next page, since we have artificially increased the page size to peek for the next page.

Compute the previous page requires a separate query to be executed, since it is not possible to check for a previous page without querying the data set.

LINQ do not allow to create subselect, so we need to fire two separate queries.

It could be possible to get rid of this second query, but it would require a more specialized provider.

However the Previous query will be executed only when a cursor is set, otherwise we are at the beginning, and no previous page exists.

More on this in the footer note.

## Connection with GraphQL for .NET

The library ```CursorPagination.Linq.GraphQL```, adds ```ToGraphQLConnection```.

```csharp
using System.Linq.CursorPagination;
using GraphQL.Builders;

IResolveConnectionContext context;

db.Posts
    .OrderByDescending(x => x.Id)
    .TakeWhile(x => x.Id == 10)
    .Take(10)
    .ToGraphQLConnection(context, maxPageSize: 100);

```

It works the same way ```ToConnection``` does with a couple of exceptions.

It will always ```Skip(1)``` as the graphql connection it is based off the relay specifications.

It will compute ```TotalCount``` only if it is requested by the client.

```graphql
query {
  posts {
    totalCount # automatically detected
    edges { node { id } }
  }
}

query {
  posts {
    # total count will not be executed
    edges { node { id } }
  }
}
```

> You need to use a ```CursorSerializer```, wich is a specialized cursor provider to deal string cursors.

## A real-world example in ASPNET Core

```csharp
[HttpGet]
public IActionResult GetPosts(int limit, string cursor, bool backwards, bool oldest)
{
    // it is pointless since take with a negative limit is allowed
    // but there are some legacy stuff we need to deal with
    if (limit < 0) throw new ArgumentException("Do not use negative limits, use backwards instead");

    (int idCursor, DateTime createdAtCursor) = this.DeserializeCursor(cursor);

    // create an sorted query
    var q = db.Posts
        .OrderByDescending(post => post.Id)
        .ThenByDescending(post => post.CreatedAt);

    // skip the cursor
    if (this.alwaysSkipCursor) {
        q = q.Skip(1);
    }

    // make sure we don't blow up the server like the last time
    limit = Math.Clamp(limit, -100, 100);

    // go backwards manually, no need to call Reverse()
    if (backwards) {
        q = q.Take(-1 * limit);
    }

    if (oldest) {
        q = q
            .OrderBy(post => post.Id)
            .ThenBy(post => post.CreatedAt);
    }

    // some random filtering
    q = this.ApplyFilters(q);

    // set the cursor, since we do this manually, and the original order is OrderByDescending
    // we use the < operator to remember ourselves that by default we have sort descending
    q = q.TakeWhile(x => x.Id < idCursor);

    // we use >= to show that do not matter what we use
    q = q.TakeWhile(x => x.CreatedAt >= createdAtCursor);

    // ToNodeList will return a flat list of nodes, whitout cursors
    return Ok(q.ToNodeList());
}

```

## General considerations

To achieve a better implementation when working with the Entity Framework Core, a specialized IQueryProvider is needed, however this library provide none.

This library try to be agnostic works with what LINQ already provide, it does not care (up to some degree) about what SQL engine you use.

As a result, when paginating, this library execute two queries, one for get the result set, and the other for check to see if there is a previous result.

### +1 trick

It is trivial to check for the next page, if we have a limit, increment by 1, if the result set is limit + 1, then we have a next page, remove the last item from the set, and return.

This is what this library does under the hood.

### +2 trick

When relay pagination, you may think that if we do the +1 trick to check for the next page, we could use the current cursor to check if there are previous results by take +2 instead of +1, since technically, the current cursor belongs to the previous set.

However this is not always true, since there is no guarantee that the cursor will exists, since it can be deleted at any moment.

If a cursor with the form ```Id == 10``` and we do ```Id <= 10``` and ```Take(take + 2)```, now we included the cursor, if the result set start with an item with the ```Id``` of ```10```, we know we have a previous page.

We could also check for ```items.Count == take + 2``` and we now know we have both previous and next pages.

However all of the above fails if we delete the record with the id of 10.

### Comparison with SQL

The following code:

```csharp
db.Posts
    .OrderByDescending(x => x.Id)
    .TakeWhile(x => x.Id == 10)
    .Take(10)
    .ToConnection(withTotalCount: true);
```

will execute these 3 queries:

```sql
-- total count, wich is optional
SELECT COUNT(*) FROM "Posts" WHERE "Id" <= 10

-- has previous, notice the logical operator '>' is the opposite of '<='
SELECT EXISTS (SELECT 1 FROM "Posts" WHERE "Id" > 10)

-- the result query
SELECT "Id", "CreatedAt" FROM "Posts" WHERE "Id" <= 10 ORDER BY "Id" DESC LIMIT 10
```

Lets first take a closer look at the queries.

```sql
SELECT COUNT(*) FROM "Posts" WHERE "Id" <= 10
SELECT       *  FROM "Posts" WHERE "Id" <= 10 ORDER BY "Id" DESC LIMIT 10
```

You will notice that total count and the result query are the same, with a small but important detail differing them.

On the total count query the limit, in this case ```LIMIT 10```, is not applied, that to be expected we are counting the whole set.

But ```ORDER BY``` is also missing, that is a crucial part of the cursor pagination, since we do not need to sort the whole set, a cursor operation is much faster to execute when taking a chunk from the middle, while the classical ```OFFSET``` pagination must always sort the set first, then jump to a point, then take a slice of it.

The same is true for checking for the existence of the previous element, the operation will usually runs very fast.

However the 3 queries are executed separately, meaning the database will not have a chance to do its magic, its important to remember that, a total count is totally optional, and ```OFFSET``` pagination is drastically slower as the offset increases.

An alternative single query would be:

```sql
SELECT
    (SELECT COUNT(*) FROM "Posts" WHERE "Id" <= 10) as "_TotalCount",
    (SELECT EXISTS (SELECT 1 FROM "Posts" WHERE "Id" > 10)) as "_HasPrevious",

    "Id", "CreatedAt" FROM "Posts" WHERE "Id" <= 10 ORDER BY "Id" DESC LIMIT 10;
```

That is not possible to execute without a custom query provider.

In the real world, we need things that are the bare bones minimum, then things that are usable, then things that work well, then things that may can work faster, in this order. Put things that are secure anywhere in the middle and there you go.

Executing a single query is nice, we do not need 3 round trips to the server, however better having something that works, that we can play around with, than having nothing at all and scratch our head wandering around the web at 2 AM looking for someone who have actually implemented, for then only finding articles that tell us how easy it is, but we found no library.

## Thank you, internet

This library, including this document, has been written by Lucas Greci and may contain some easter eggs.
