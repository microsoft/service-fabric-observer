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

            this.buffer = new List<T>(new T[capacity]);
            this.head = capacity - 1;
        }

        /// <summary>
        /// Gets the number of elements in the collection.
        /// </summary>
        public int Count { get; private set; }

        /// <summary>
        /// Gets a value indicating whether or not the collection is readonly.
        /// </summary>
        public bool IsReadOnly { get; } = false;

        /// <summary>
        /// Gets or sets the capacity of the collection.
        /// </summary>
        public int Capacity
        {
            get => this.buffer.Count;
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), $"value must be greater than 0 ({value}).");
                }

                if (value == this.buffer.Count)
                {
                    return;
                }

                var buffer1 = new T[value];
                var count = 0;

                while (this.Count > 0 && count < value)
                {
                    buffer1[count++] = this.Dequeue();
                }

                this.buffer = buffer1;
                this.Count = count;
                this.head = count - 1;
                this.tail = 0;
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
                if (index < 0 || index >= this.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return this.buffer[(this.tail + index) % this.Capacity];
            }

            set
            {
                if (index < 0 || index >= this.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                this.buffer[(this.tail + index) % this.Capacity] = value;
            }
        }

        /// <summary>
        /// Removes an item from the Circular buffer collection.
        /// </summary>
        /// <param name="item">Item to remove.</param>
        /// <returns>Boolean representing success or failure of operation.</returns>
        public bool Remove(T item)
        {
            if (!this.Contains(item))
            {
                return false;
            }

            this.RemoveAt(this.IndexOf(item));
            this.Dequeue();

            return true;
        }

        /// <summary>
        /// Places an item into the list queue.
        /// </summary>
        /// <param name="item">Item to enqueue.</param>
        /// <returns>Item that was enqueued.</returns>
        public T Enqueue(T item)
        {
            this.head = (this.head + 1) % this.Capacity;
            var overwritten = this.buffer[this.head];
            this.buffer[this.head] = item;

            if (this.Count == this.Capacity)
            {
                this.tail = (this.tail + 1) % this.Capacity;
            }
            else
            {
                ++this.Count;
            }

            return overwritten;
        }

        /// <summary>
        /// Removes an item from the list queue.
        /// </summary>
        /// <returns>Item that was dequeued.</returns>
        public T Dequeue()
        {
            if (this.Count == 0)
            {
                throw new InvalidOperationException("queue exhausted");
            }

            var dequeued = this.buffer[this.tail];
            this.buffer[this.tail] = default(T);
            this.tail = (this.tail + 1) % this.Capacity;
            --this.Count;

            return dequeued;
        }

        /// <summary>
        /// Adds an item to the list.
        /// </summary>
        /// <param name="item">Item to be added.</param>
        public void Add(T item)
        {
            this.Enqueue(item);
        }

        /// <summary>
        /// Resets all elements of internal buffer to default value (0).
        /// Resets head/tail/Count to default values.
        /// </summary>
        public void Clear()
        {
            this.head = this.Capacity - 1;
            this.tail = 0;
            this.Count = 0;
            buffer = new List<T>(new T[Capacity]);
        }

        /// <summary>
        /// Gets a value indicating whether or not the list contains the item.
        /// </summary>
        /// <param name="item">The item to look for in the list.</param>
        /// <returns>Boolean value.</returns>
        public bool Contains(T item)
        {
            return this.buffer.Contains(item);
        }

        /// <summary>
        /// Copies all elements from List to supplied target array at given index.
        /// </summary>
        /// <param name="array">The target array to copy to.</param>
        /// <param name="index">The index of the target array to start copying to.</param>
        public void CopyTo(T[] array, int index)
        {
            this.buffer.CopyTo(array, index);
        }

        /// <summary>
        /// Returns the index of the the supplied item in the List.
        /// </summary>
        /// <param name="item">The item to look for.</param>
        /// <returns>Index of item if found. Else, -1 if not found.</returns>
        public int IndexOf(T item)
        {
            for (var i = 0; i < this.Count; ++i)
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
            if (index < 0 || index > this.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    $"invalid index supplied: {index}");
            }

            if (this.Count == index)
            {
                this.Enqueue(item);
            }
            else
            {
                var last = this[this.Count - 1];

                for (var i = index; i < this.Count - 2; ++i)
                {
                    this[i + 1] = this[i];
                }

                this[index] = item;

                this.Enqueue(last);
            }
        }

        /// <summary>
        /// Removes an element from the List.
        /// </summary>
        /// <param name="index">Index of the element in the List to remove.</param>
        public void RemoveAt(int index)
        {
            if (index < 0 || index >= this.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    $"invalid index supplied: {index}");
            }

            for (var i = index; i > 0; --i)
            {
                this[i] = this[i - 1];
            }

            this.Dequeue();
        }

        /// <summary>
        /// Gets the enumerator for the List.
        /// </summary>
        /// <returns>Enumerator.</returns>
        public IEnumerator<T> GetEnumerator()
        {
            if (this.Count == 0 || this.Capacity == 0)
            {
                yield break;
            }

            for (var i = 0; i < this.Count; ++i)
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
            return this.GetEnumerator();
        }
    }
}
