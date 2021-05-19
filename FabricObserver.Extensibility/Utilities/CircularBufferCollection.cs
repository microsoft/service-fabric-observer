// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Generic Circular buffer implementation for IList of numeric type T.
    /// CircularBufferCollection class based on public (non-license-protected) sample: http://www.geekswithblogs.net/blackrob/archive/2014/09/01/circular-buffer-in-c.aspx
    /// All observers that produce numeric data as part of their resource usage monitoring
    /// use this class to store their data (held within instances of FabricResourceUsageData,
    /// see FRUD's Data member). Constraint on struct is partial, but useful.
    /// </summary>
    /// <typeparam name="T">Numeric type.</typeparam>
    public class CircularBufferCollection<T> : IList<T>
            where T : struct
    {
        private IList<T> buffer;
        private int head;
        private int tail;

        /// <summary>
        /// Initializes a new instance of the <see cref="CircularBufferCollection{T}"/> class.
        /// </summary>
        /// <param name="capacity">Fixed size of collection.</param>
        public CircularBufferCollection(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentException("You must provide a fixed capacity that is greater than 0.");
            }

            buffer = new List<T>(new T[capacity]);
            head = capacity - 1;
        }

        /// <summary>
        /// Gets the number of elements in the collection.
        /// </summary>
        public int Count
        {
            get; private set;
        }

        /// <summary>
        /// Gets a value indicating whether or not the collection is readonly.
        /// </summary>
        public bool IsReadOnly 
        { 
            get; 
        } = false;

        /// <summary>
        /// Gets or sets the capacity of the collection.
        /// </summary>
        public int Capacity
        {
            get => buffer.Count;
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), $"value must be greater than 0 ({value}).");
                }

                if (value == buffer.Count)
                {
                    return;
                }

                var buffer1 = new T[value];
                var count = 0;

                while (Count > 0 && count < value)
                {
                    buffer1[count++] = Dequeue();
                }

                buffer = buffer1;
                Count = count;
                head = count - 1;
                tail = 0;
            }
        }

        /// <summary>
        /// Indexer.
        /// </summary>
        /// <param name="index">Index of element in the list.</param>
        /// <returns>Element value.</returns>
        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return buffer[(tail + index) % Capacity];
            }

            set
            {
                if (index < 0 || index >= Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                buffer[(tail + index) % Capacity] = value;
            }
        }

        /// <summary>
        /// Removes an item from the Circular buffer collection.
        /// </summary>
        /// <param name="item">Item to remove.</param>
        /// <returns>Boolean representing success or failure of operation.</returns>
        public bool Remove(T item)
        {
            if (!Contains(item))
            {
                return false;
            }

            RemoveAt(IndexOf(item));
            Dequeue();

            return true;
        }

        /// <summary>
        /// Places an item into the list queue.
        /// </summary>
        /// <param name="item">Item to enqueue.</param>
        /// <returns>Item that was enqueued.</returns>
        public T Enqueue(T item)
        {
            head = (head + 1) % Capacity;
            var overwritten = buffer[head];
            buffer[head] = item;

            if (Count == Capacity)
            {
                tail = (tail + 1) % Capacity;
            }
            else
            {
                ++Count;
            }

            return overwritten;
        }

        /// <summary>
        /// Removes an item from the list queue.
        /// </summary>
        /// <returns>Item that was dequeued.</returns>
        public T Dequeue()
        {
            if (Count == 0)
            {
                throw new InvalidOperationException("queue exhausted");
            }

            var dequeued = buffer[tail];
            buffer[tail] = default;
            tail = (tail + 1) % Capacity;
            --Count;

            return dequeued;
        }

        /// <summary>
        /// Adds an item to the list.
        /// </summary>
        /// <param name="item">Item to be added.</param>
        public void Add(T item)
        {
            Enqueue(item);
        }

        /// <summary>
        /// Resets all elements of internal buffer to default value (0).
        /// Resets head/tail/Count to default values.
        /// </summary>
        public void Clear()
        {
            head = Capacity - 1;
            tail = 0;
            Count = 0;
            buffer = new List<T>(new T[Capacity]);
        }

        /// <summary>
        /// Gets a value indicating whether or not the list contains the item.
        /// </summary>
        /// <param name="item">The item to look for in the list.</param>
        /// <returns>Boolean value.</returns>
        public bool Contains(T item)
        {
            return buffer.Contains(item);
        }

        /// <summary>
        /// Copies all elements from List to supplied target array at given index.
        /// </summary>
        /// <param name="array">The target array to copy to.</param>
        /// <param name="index">The index of the target array to start copying to.</param>
        public void CopyTo(T[] array, int index)
        {
            buffer.CopyTo(array, index);
        }

        /// <summary>
        /// Returns the index of the the supplied item in the List.
        /// </summary>
        /// <param name="item">The item to look for.</param>
        /// <returns>Index of item if found. Else, -1 if not found.</returns>
        public int IndexOf(T item)
        {
            for (var i = 0; i < Count; ++i)
            {
                if (Equals(item, this[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Inserts an element into the List.
        /// </summary>
        /// <param name="index">Location in list to insert element.</param>
        /// <param name="item">Element to insert into list.</param>
        public void Insert(int index, T item)
        {
            if (index < 0 || index > Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    $"invalid index supplied: {index}");
            }

            if (Count == index)
            {
                Enqueue(item);
            }
            else
            {
                var last = this[Count - 1];

                for (var i = index; i < Count - 2; ++i)
                {
                    this[i + 1] = this[i];
                }

                this[index] = item;

                Enqueue(last);
            }
        }

        /// <summary>
        /// Removes an element from the List.
        /// </summary>
        /// <param name="index">Index of the element in the List to remove.</param>
        public void RemoveAt(int index)
        {
            if (index < 0 || index >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), $"invalid index supplied: {index}");
            }

            for (var i = index; i > 0; --i)
            {
                this[i] = this[i - 1];
            }

            Dequeue();
        }

        /// <summary>
        /// Gets the enumerator for the List.
        /// </summary>
        /// <returns>Enumerator.</returns>
        public IEnumerator<T> GetEnumerator()
        {
            if (Count == 0 || Capacity == 0)
            {
                yield break;
            }

            for (var i = 0; i < Count; ++i)
            {
                yield return this[i];
            }
        }

        /// <summary>
        /// Gets the enumerator for the List. Explicit interface definition.
        /// </summary>
        /// <returns>Enumerator.</returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
