using System;
using System.Collections.Generic;

/// <summary>IHashable represents an object which
/// can be inserted into a Spatial Hash and queried
/// using a key (two cell indices)</summary>
public interface IHashable
{
    public (CellIndex, CellIndex)? RegisteredHashBounds { get; set; }
    public int QueryId { get; set; }
}

/// <summary>Vector2 represents an object's position
/// on a 2D plane</summary>
public ref struct Vector2
{
    public float X;
    public float Y;

    public Vector2(float x, float y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>CellIndex repesents a position as
/// coordinates on a grid</summary>
public struct CellIndex
{
    public int X;
    public int Y;

    public CellIndex(int x, int y)
    {
        X = x;
        Y = y; 
    }

    public static bool operator ==(CellIndex a, CellIndex b)
    {
        return a.X == b.X && a.Y == b.Y;
    }

    public static bool operator !=(CellIndex a, CellIndex b)
    {
        return a.X != b.X || a.Y != b.Y;
    }

    public override bool Equals(object obj)
    {
        if (!(obj is CellIndex))
        {
            return false;
        }

        return Equals((CellIndex)obj);
    }

    public bool Equals(CellIndex other)
    {
        return X == other.X && Y == other.Y;
    }

    public override int GetHashCode()
    {
        return GenerateHashCode((int)X, (int)Y);
    }

    private static int GenerateHashCode(int x, int y)
    {
        return (x << 2) ^ y;
    }
}

/// <summary>A fast fixed-size Spatial Hash implementation</summary>
public class SpatialHash
{
    /// <summary>Multidimensional array as [x, y] to represent grid position</summary>
    public List<IHashable>[,] Grid;

    /// <summary>Cached previous query result
    /// to prevent allocating new list on every hash query.
    /// List o(n) will be most efficient dynamic collection
    /// for low expected item count (< 10).</summary>
    private List<IHashable> _queryBucket;
    /// <summary>Unique identifier to deduplicate colliders
    /// that exist in multiple buckets in a single query</summary>
    private int _queryId;

    public int Width;
    public int Height;

    /// <summary>Size represented by pixels</summary>
    public int CellSize;

    public SpatialHash(int width, int height, int cellSize)
    {
        Grid = new List<IHashable>[width, height];
        _queryBucket = new List<IHashable>();

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                Grid[x, y] = new List<IHashable>();
            }
        }

        Width = width;
        Height = height;

        CellSize = cellSize;
    }

    /// <summary>Returns a 2D position as an index within the grid space</summary>
    /// <param name="position">World position of hashed object</param>
    public CellIndex GetCellIndex(Vector2 position)
    {
        float inverseCellSize = 1f / CellSize;

        int x = (int)Math.Floor(position.X * inverseCellSize);
        int y = (int)Math.Floor(position.Y * inverseCellSize);

        /***
         * Calculated index clamped between 0 and total # of cells
         * to prevent index going out of bounds
         */
        int clampedX = Math.Min(Width - 1, Math.Max(0, x));
        int clampedY = Math.Min(Height - 1, Math.Max(0, y));

        return new CellIndex(clampedX, clampedY);
    }

    public void Insert(IHashable collider, Vector2 topLeftBounds, Vector2 bottomRightBounds)
    {
        CellIndex startCoordinates = GetCellIndex(topLeftBounds);
        CellIndex endCoordinates = GetCellIndex(bottomRightBounds);

        collider.RegisteredHashBounds = (startCoordinates, endCoordinates);

        /***
         * Use top left and bottom right corners of collider bounds
         * to find every cell in between that the collider belongs in
         */
        for (int x0 = startCoordinates.X, x1 = endCoordinates.X; x0 <= x1; x0++)
        {
            for (int y0 = startCoordinates.Y, y1 = endCoordinates.Y; y0 <= y1; y0++)
            {
                Grid[x0, y0].Add(collider);
            }
        }
    }

    public void Remove(IHashable collider)
    {
        if (collider.RegisteredHashBounds != null)
        {
            /***
            * Need to explicitly coerce bounds as non-null tuple
            * because IHashable type is nullable
            */
            (CellIndex, CellIndex) colliderHashBounds = ((CellIndex, CellIndex))collider.RegisteredHashBounds;

            CellIndex startCoordinates = colliderHashBounds.Item1;
            CellIndex endCoordinates = colliderHashBounds.Item2;

            collider.RegisteredHashBounds = null;

            for (int x0 = startCoordinates.X, x1 = endCoordinates.X; x0 <= x1; x0++)
            {
                for (int y0 = startCoordinates.Y, y1 = endCoordinates.Y; y0 <= y1; y0++)
                {
                    Grid[x0, y0].Remove(collider);
                }
            }
        }
    }

    public void UpdateCollider(IHashable collider, Vector2 topLeftBounds, Vector2 bottomRightBounds)
    {
        /***
         * Do not need to update hashed collider if bounds have not moved enough
         * to change cells
         */
        if (ColliderHasMovedCells(collider, topLeftBounds, bottomRightBounds))
        {
            Remove(collider);
            Insert(collider, topLeftBounds, bottomRightBounds);
        }
    }

    public bool ColliderHasMovedCells(IHashable collider, Vector2 topLeftBounds, Vector2 bottomRightBounds)
    {
        CellIndex startCoordinates = GetCellIndex(topLeftBounds);
        CellIndex endCoordinates = GetCellIndex(bottomRightBounds);

        return collider.RegisteredHashBounds != (startCoordinates, endCoordinates);
    }

    /// <summary>Returns all colliders an entity shares a bucket with (with no repeat and self not returned)</summary>
    /// <param name="collider">Target collider</param>
    /// <param name="radius">Amount of additional cells to check in every direction</param>
    public List<IHashable> FindNearbyColliders(IHashable collider, int radius)
    {
        /***
         * Clear previous query to save memory from a new List allocation
         * NOTE: memory does not get released by Clear and may still build up on GC
         */
        _queryBucket.Clear();

        if (collider.RegisteredHashBounds != null)
        {
            (CellIndex, CellIndex) colliderHashBounds = ((CellIndex, CellIndex))collider.RegisteredHashBounds;

            int startX = Math.Max(0, colliderHashBounds.Item1.X - radius);
            int startY = Math.Max(0, colliderHashBounds.Item1.Y - radius);
            int endX = Math.Min(Width - 1, colliderHashBounds.Item2.X + radius);
            int endY = Math.Min(Height - 1, colliderHashBounds.Item2.Y + radius);

            /***
            * Iterate to ensure unique query id
            */
            int queryId = _queryId++;

            for (int x0 = startX, x1 = endX; x0 <= x1; x0++)
            {
                for (int y0 = startY, y1 = endY; y0 <= y1; y0++)
                {
                    foreach (IHashable coll in Grid[x0, y0])
                    {
                        if (coll.QueryId != queryId &&
                            coll.RegisteredHashBounds != collider.RegisteredHashBounds)
                        {
                            /***
                            * Set collider query id to current query id to prevent
                            * duplicate object from same query in nearby bucket
                            */
                            coll.QueryId = queryId;
                            _queryBucket.Add(coll);
                        }
                    }
                }
            }
        }

        return _queryBucket;
    }
}