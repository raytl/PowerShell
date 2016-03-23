/********************************************************************++
 * Copyright (c) Microsoft Corporation.  All rights reserved.
 * --********************************************************************/

using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics.CodeAnalysis; // for fxcop
using System.Reflection;
using System.Runtime.Serialization;
using Dbg = System.Management.Automation.Diagnostics;

#if CORECLR
// Use stub for SerializableAttribute and ISerializable related types.
using Microsoft.PowerShell.CoreClr.Stubs;
#endif

namespace System.Management.Automation
{
    #region DataAddedEventArgs

    /// <summary>
    /// Event arguments passed to PSDataCollection DataAdded handlers.
    /// </summary>
    public sealed class DataAddedEventArgs : EventArgs
    {
        #region Private Data

        private int index;
        private Guid psInstanceId;
  
        #endregion

        #region Constructor

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="psInstanceId">
        /// PowerShell InstanceId which added this data.
        /// Guid.Empty, if the data is not added by a PowerShell
        /// instance.
        /// </param>
        /// <param name="index">
        /// Index at which the data is added.
        /// </param>
        internal DataAddedEventArgs(Guid psInstanceId, int index)
        {
            this.psInstanceId = psInstanceId;
            this.index = index;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Index at which the data is added.
        /// </summary>
        public int Index { get { return this.index; } }

        /// <summary>
        /// PowerShell InstanceId which added this data.
        /// Guid.Empty, if the data is not added by a PowerShell
        /// instance.
        /// </summary>
        public Guid PowerShellInstanceId { get { return this.psInstanceId; } }

        #endregion
    }

    #endregion

    /// <summary>
    /// Event arguments passed to PSDataCollection DataAdding handlers.
    /// </summary>
    public sealed class DataAddingEventArgs : EventArgs
    {
        #region Private Data

        private Guid psInstanceId;
        private Object itemAdded;
  
        #endregion

        #region Constructor

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="psInstanceId">
        /// PowerShell InstanceId which added this data.
        /// Guid.Empty, if the data is not added by a PowerShell
        /// instance.
        /// </param>
        /// <param name="itemAdded">
        /// The actual item about to be added.
        /// </param>
        internal DataAddingEventArgs(Guid psInstanceId, object itemAdded)
        {
            this.psInstanceId = psInstanceId;
            this.itemAdded = itemAdded;
        }

        #endregion

        #region Properties

        /// <summary>
        /// The item about to be added.
        /// </summary>
        public Object ItemAdded { get { return this.itemAdded; } }

        /// <summary>
        /// PowerShell InstanceId which added this data.
        /// Guid.Empty, if the data is not added by a PowerShell
        /// instance.
        /// </summary>
        public Guid PowerShellInstanceId { get { return this.psInstanceId; } }

        #endregion
    }

    #region PSDataCollection

    /// <summary>build
    /// Thread Safe buffer used with PowerShell Hosting interfaces.
    /// </summary>
    [Serializable]
    public class PSDataCollection<T> : IList<T>, ICollection<T>, IEnumerable<T>, IList, ICollection, IEnumerable, IDisposable, ISerializable
    {
        #region Private Data

        private IList<T> data;
        private ManualResetEvent readWaitHandle;
        private bool isOpen = true;
        private bool releaseOnEnumeration;
        private bool isEnumerated;
        // a counter to keep track of active PowerShell instances
        // using this buffer.
        private int refCount;

        private object syncObject = new object();
        private bool isDisposed = false;

        /// <summary>
        /// Whether the enumerator needs to be blocking
        /// by default
        /// </summary>
        private bool _blockingEnumerator = false;

        /// <summary>
        /// whether the ref count was incremented when 
        /// BlockingEnumerator was updated
        /// </summary>
        private bool _refCountIncrementedForBlockingEnumerator = false;

        private int _countNewData = 0;
        private int _dataAddedFrequency = 1;
        private Guid _sourceGuid = Guid.Empty;

        #endregion

        #region Public Constructors

        /// <summary>
        /// Default Constructor
        /// </summary>
        public PSDataCollection() : this(new List<T>())
        {
        }

        /// <summary>
        /// Creates a PSDataCollection that includes all the items in the IEnumerable and invokes Complete(). 
        /// </summary>
        /// <param name="items">
        /// Items used to initialize the collection
        /// </param>
        /// <remarks>
        /// This constructor is useful when the user wants to use an IEnumerable as an input to one of the PowerShell.BeginInvoke overloads.
        /// The invocation doesn't complete until Complete() is called on the PSDataCollection; this constructor does the Complete() on
        /// behalf of the user.
        /// </remarks>
        public PSDataCollection(IEnumerable<T> items) : this(new List<T>(items))
        {
            this.Complete();
        }

        /// <summary>
        /// Initializes a new instance with the specified capacity
        /// <paramref name="capacity"/>
        /// </summary>
        /// <param name="capacity">
        /// The number of elements that the new buffer can initially
        /// store.
        /// </param>
        /// <remarks>
        /// Capacity is the number of elements that the PSDataCollection can 
        /// store before resizing is required.
        /// </remarks>
        public PSDataCollection(int capacity) : this(new List<T>(capacity))
        {
        }

        #endregion

        #region type converters

        /// <summary>
        /// Wrap the argument in a PSDataCollection
        /// </summary>
        /// <param name="valueToConvert">The value to convert</param>
        /// <returns>New collection of value, marked as Complete</returns>
        [SuppressMessage("Microsoft.Usage", "CA2225:OperatorOverloadsHaveNamedAlternates",
            Justification = "There are already alternates to the implicit casts, ToXXX and FromXXX methods are unnecessary and redundant")]
        public static implicit operator PSDataCollection<T>(bool valueToConvert)
        {
            return CreateAndInitializeFromExplicitValue(valueToConvert);
        }

        /// <summary>
        /// Wrap the argument in a PSDataCollection
        /// </summary>
        /// <param name="valueToConvert">The value to convert</param>
        /// <returns>New collection of value, marked as Complete</returns>
        [SuppressMessage("Microsoft.Usage", "CA2225:OperatorOverloadsHaveNamedAlternates",
            Justification = "There are already alternates to the implicit casts, ToXXX and FromXXX methods are unnecessary and redundant")]
        public static implicit operator PSDataCollection<T>(string valueToConvert)
        {
            return CreateAndInitializeFromExplicitValue(valueToConvert);
        }

        /// <summary>
        /// Wrap the argument in a PSDataCollection
        /// </summary>
        /// <param name="valueToConvert">The value to convert</param>
        /// <returns>New collection of value, marked as Complete</returns>
        [SuppressMessage("Microsoft.Usage", "CA2225:OperatorOverloadsHaveNamedAlternates",
            Justification = "There are already alternates to the implicit casts, ToXXX and FromXXX methods are unnecessary and redundant")]
        public static implicit operator PSDataCollection<T>(int valueToConvert)
        {
            return CreateAndInitializeFromExplicitValue(valueToConvert);
        }

        /// <summary>
        /// Wrap the argument in a PSDataCollection
        /// </summary>
        /// <param name="valueToConvert">The value to convert</param>
        /// <returns>New collection of value, marked as Complete</returns>
        [SuppressMessage("Microsoft.Usage", "CA2225:OperatorOverloadsHaveNamedAlternates",
            Justification = "There are already alternates to the implicit casts, ToXXX and FromXXX methods are unnecessary and redundant")]
        public static implicit operator PSDataCollection<T>(byte valueToConvert)
        {
            return CreateAndInitializeFromExplicitValue(valueToConvert);
        }

        private static PSDataCollection<T> CreateAndInitializeFromExplicitValue(object valueToConvert)
        {
            PSDataCollection<T> psdc = new PSDataCollection<T>();
            psdc.Add(LanguagePrimitives.ConvertTo<T>(valueToConvert));
            psdc.Complete();
            return psdc;
        }

        /// <summary>
        /// Wrap the argument in a PSDataCollection
        /// </summary>
        /// <param name="valueToConvert">The value to convert</param>
        /// <returns>New collection of value, marked as Complete</returns>
        [SuppressMessage("Microsoft.Usage", "CA2225:OperatorOverloadsHaveNamedAlternates",
            Justification = "There are already alternates to the implicit casts, ToXXX and FromXXX methods are unnecessary and redundant")]       
        public static implicit operator PSDataCollection<T>(Hashtable valueToConvert)
        {
            PSDataCollection<T> psdc = new PSDataCollection<T>();
            psdc.Add(LanguagePrimitives.ConvertTo<T>(valueToConvert));
            psdc.Complete();
            return psdc;
        }

        /// <summary>
        /// Wrap the argument in a PSDataCollection
        /// </summary>
        /// <param name="valueToConvert">The value to convert</param>
        /// <returns>New collection of value, marked as Complete</returns>
        [SuppressMessage("Microsoft.Usage", "CA2225:OperatorOverloadsHaveNamedAlternates",
            Justification = "There are already alternates to the implicit casts, ToXXX and FromXXX methods are unnecessary and redundant")]        
        public static implicit operator PSDataCollection<T>(T valueToConvert)
        {
            PSDataCollection<T> psdc = new PSDataCollection<T>();
            psdc.Add(LanguagePrimitives.ConvertTo<T>(valueToConvert));
            psdc.Complete();
            return psdc;
        }

        /// <summary>
        /// Wrap the argument in a PSDataCollection
        /// </summary>
        /// <param name="arrayToConvert">The value to convert</param>
        /// <returns>New collection of value, marked as Complete</returns>
        [SuppressMessage("Microsoft.Usage", "CA2225:OperatorOverloadsHaveNamedAlternates",
            Justification = "There are already alternates to the implicit casts, ToXXX and FromXXX methods are unnecessary and redundant")]       
        public static implicit operator PSDataCollection<T>(object[] arrayToConvert)
        {
            PSDataCollection<T> psdc = new PSDataCollection<T>();
            if (arrayToConvert != null)
            {
                foreach (var ae in arrayToConvert)
                {
                    psdc.Add(LanguagePrimitives.ConvertTo<T>(ae));
                }
            }
            psdc.Complete();
            return psdc;
        }

        #endregion

        #region Internal Constructor

        /// <summary>
        /// Construct the DataBuffer using the supplied <paramref name="listToUse"/>
        /// as the data buffer.
        /// </summary>
        /// <param name="listToUse">
        /// buffer wherer the elements are stored
        /// </param>
        /// <remarks>
        /// Using this constructor will make the data buffer a wrapper on
        /// top of the <paramref name="listToUse"/>, which provides synchronized
        /// access.
        /// </remarks>
        internal PSDataCollection(IList<T> listToUse)
        {
            data = listToUse;
        }

        /// <summary>
        /// Creates a PSDataCollection from an ISerializable context
        /// </summary>
        /// <param name="info">Serialization information for this instance</param>
        /// <param name="context">The streaming context for this instance</param>
        protected PSDataCollection(SerializationInfo info, StreamingContext context)
        {
            if (info == null)
            {
                throw PSTraceSource.NewArgumentNullException("info");
            }

            IList<T> listToUse = info.GetValue("Data", typeof(IList<T>)) as IList<T>;

            if (listToUse == null)
            {
                throw PSTraceSource.NewArgumentNullException("info");
            }

            data = listToUse;

            _blockingEnumerator = info.GetBoolean("BlockingEnumerator");
            _dataAddedFrequency = info.GetInt32("DataAddedCount");
            EnumeratorNeverBlocks = info.GetBoolean("EnumeratorNeverBlocks");
            isOpen = info.GetBoolean("IsOpen");
        }

        #endregion

        #region PSDataCollection Specific Public Methods / Properties

        /// <summary>
        /// Event fired when objects are being added to the underlying buffer.
        /// </summary>
        public event EventHandler<DataAddingEventArgs> DataAdding;

        /// <summary>
        /// Event fired when objects are done being added to the underlying buffer
        /// </summary>
        public event EventHandler<DataAddedEventArgs> DataAdded;

        /// <summary>
        /// Event fired when the buffer is completed.
        /// </summary>
        public event EventHandler Completed;

        /// <summary>
        /// A boolean which determines if the buffer is open.
        /// </summary>
        public bool IsOpen
        {
            get
            {
                lock (syncObject)
                {
                    return isOpen;
                }
            }
        }

        /// <summary>
        /// An int that tells the frequency of Data Added events fired.
        /// Raises the DataAdded event only when data has been added a multiple of this many times,
        /// or when collection can receive no more data, if further data is added past the last event
        /// prior to completion.
        /// </summary>
        public int DataAddedCount
        {
            get { return _dataAddedFrequency; }
            set
            {
                bool raiseDataAdded = false;
                lock (syncObject)
                {
                    _dataAddedFrequency = value;
                    if (_countNewData >= _dataAddedFrequency)
                    {
                        raiseDataAdded = true;
                        _countNewData = 0;
                    }
                }

                if (raiseDataAdded)
                {
                    // We should raise the event outside of the lock
                    // as the call is made into 3rd party code
                    RaiseDataAddedEvent(_lastPsInstanceId, _lastIndex);
                }
            }
        }

        /// <summary>
        /// Serializes all input by default.
        /// This is supported only for PSDataCollections of PSObject.
        /// </summary>
        public bool SerializeInput
        {
            get
            {
                return serializeInput;
            }

            set
            {
                if (typeof(T) != typeof(PSObject))
                {
                    // If you drop this constraint, GetSerializedInput must be updated.
                    throw new NotSupportedException(PSDataBufferStrings.SerializationNotSupported);
                }

                serializeInput = value;
            }
        }
        private bool serializeInput = false;

        /// <summary>
        /// Determines whether this PSDataCollection was created implicitly in support of
        /// data collection (for example, a workflow that wants to capture output but hasn't
        /// provided an instance of the PSDataCollection to capture it with.)
        /// </summary>
        public bool IsAutoGenerated
        {
            get; set;
        }

        /// <summary>
        /// Internal tag for indicating a source object identifier for this collection.
        /// </summary>
        internal Guid SourceId
        {
            get
            {
                lock (syncObject)
                {
                    return _sourceGuid;
                }
            }
            set
            {
                lock (syncObject)
                {
                    _sourceGuid = value;
                }
            }
        }

        /// <summary>
        /// If this flag is set to true, the items in the collection will be set to null when it is
        /// traversed using a PSDataCollectionEnumerator
        /// </summary>
        internal bool ReleaseOnEnumeration
        {
            get
            {
                lock (syncObject)
                {
                    return this.releaseOnEnumeration;
                }
            }
            set
            {
                lock (syncObject)
                {
                    this.releaseOnEnumeration = value;
                }
            }
        }

        /// <summary>
        /// This flag is true when the collection has been enumerated at least once by a PSDataCollectionEnumerator
        /// </summary>
        internal bool IsEnumerated
        {
            get
            {
                lock (syncObject)
                {
                    return this.isEnumerated;
                }
            }
            set
            {
                lock (syncObject)
                {
                    this.isEnumerated = value;
                }
            }
        }

        /// <summary>
        /// Completes insertions to the buffer. 
        /// Subsequent Inserts to the buffer will result in an InvalidOperationException
        /// </summary>
        public void Complete()
        {
            bool raiseEvents = false;
            bool raiseDataAdded = false;
            try
            {
                // Close the buffer
                lock (syncObject)
                {
                    if (isOpen)
                    {
                        isOpen = false;
                        raiseEvents = true;
                        // release any threads to notify an event. Enumertor
                        // blocks on this syncObject.
                        Monitor.PulseAll(syncObject);

                        if (_countNewData > 0)
                        {
                            raiseDataAdded = true;
                            _countNewData = 0;
                        }
                    }
                }
            }
            finally
            {
                // raise the events outside of the lock.
                if (raiseEvents)
                {
                    if (null != readWaitHandle)
                    {
                        // unblock any readers waiting on the handle
                        readWaitHandle.Set();
                    }

                    // A temporary variable is used as the Completed may
                    // reach null (because of -='s) after the null check
                    EventHandler tempCompleted = Completed;
                    if (null != tempCompleted)
                    {
                        tempCompleted(this, EventArgs.Empty);                         
                    }
                }
                if (raiseDataAdded)
                {
                    RaiseDataAddedEvent(_lastPsInstanceId, _lastIndex);
                }
            }
        }

        /// <summary>
        /// Indicates whether the data collection should 
        /// have a blocking enumerator by default. Currently
        /// only when a PowerShell object is associated with
        /// the data collection, a reference count is added
        /// which causes the enumerator to be blocking. This
        /// prevents the use of PSDataCollection without a
        /// PowerShell object. This property fixes the same
        /// </summary>
        public bool BlockingEnumerator
        {
            get
            {
                lock (syncObject)
                {
                    return _blockingEnumerator;
                }
            }
            set
            {
                lock(syncObject)
                {
                    _blockingEnumerator = value;

                    if (_blockingEnumerator)
                    {
                        if (!_refCountIncrementedForBlockingEnumerator)
                        {
                            _refCountIncrementedForBlockingEnumerator = true;
                            AddRef();
                        }
                    }
                    else
                    {
                        // TODO: false doesn't always leading to non-blocking
                        // behavior in an intuitive way. Need to follow up
                        // and fix this
                        if(_refCountIncrementedForBlockingEnumerator)
                        {
                            _refCountIncrementedForBlockingEnumerator = false;
                            DecrementRef();
                        }
                    }
                }
            }

        }

        /// <summary>
        /// If this is set to true, then the enumerator returned from
        /// GetEnumerator() will never block.
        /// </summary>
        public bool EnumeratorNeverBlocks { get; set; }

        #endregion

        #region IList Generic Overrides

        /// <summary>
        /// Gets or sets the element at the specified index. 
        /// </summary>
        /// <param name="index">
        /// The zero-based index of the element to get or set.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Objects cannot be added to a closed buffer. 
        /// Make sure the buffer is open for Add and Insert 
        /// operations to succeed.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// index is less than 0.
        /// (or)
        /// index is equal to or greater than Count.
        /// </exception>
        public T this[int index] 
        {
            get
            {
                lock (syncObject)
                {
                    return data[index];
                }
            }
            set
            {
                lock (syncObject)
                {
                    if ((index < 0) || (index >= data.Count))
                    {
                        throw PSTraceSource.NewArgumentOutOfRangeException("index", index,
                            PSDataBufferStrings.IndexOutOfRange, 0, data.Count - 1);
                    }

                    if (serializeInput)
                    {
                        value = (T)(Object)GetSerializedObject(value);
                    }
                    data[index] = value;
                }
            }
        }

        /// <summary>
        /// Determines the index of a specific item in the buffer. 
        /// </summary>
        /// <param name="item">
        /// The object to locate in the buffer.
        /// </param>
        /// <returns>
        /// The index of item if found in the buffer; otherwise, -1. 
        /// </returns>
        public int IndexOf(T item)
        {
            lock (syncObject)
            {
                return InternalIndexOf(item);
            }
        }

        /// <summary>
        /// Inserts an item to the buffer at the specified index. 
        /// </summary>
        /// <param name="index">
        /// The zero-based index at which item should be inserted.
        /// </param>
        /// <param name="item">
        /// The object to insert into the buffer.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Objects cannot be added to a closed buffer. 
        /// Make sure the buffer is open for Add and Insert 
        /// operations to succeed.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The index specified is less than zero or greater 
        /// than Count.
        /// </exception>
        public void Insert(int index, T item)
        {
            lock (syncObject)
            {
                InternalInsertItem(Guid.Empty, index, item);
            }

            RaiseEvents(Guid.Empty, index);
        }

        /// <summary>
        /// Removes the item at the specified index.
        /// </summary>
        /// <param name="index">
        /// The zero-based index of the item to remove.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// index is not a valid index in the buffer.
        /// </exception>
        public void RemoveAt(int index)
        {
            lock (syncObject)
            {
                if ((index < 0) || (index >= data.Count))
                {
                    throw PSTraceSource.NewArgumentOutOfRangeException("index", index,
                        PSDataBufferStrings.IndexOutOfRange, 0, data.Count - 1);
                }
                RemoveItem(index);
            }
        }

        #endregion

        #region ICollection Generic Overrides

        /// <summary>
        /// Gets the number of elements contained in the buffer
        /// </summary>
        public int Count 
        {
            get
            {
                lock (syncObject)
                {
                    if (data == null)
                        return 0;
                    else
                        return data.Count;
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether the buffer is read-only.
        /// </summary>
        public bool IsReadOnly 
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Adds an item to the thread-safe buffer
        /// </summary>
        /// <param name="item">
        /// item to add
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Objects cannot be added to a closed buffer. 
        /// Make sure the buffer is open for Add and Insert 
        /// operations to succeed.
        /// </exception>
        public void Add(T item)
        {
           InternalAdd(Guid.Empty, item);
        }

        /// <summary>
        /// Removes all items from the buffer
        /// </summary>       
        public void Clear()
        {
            lock (this.syncObject)
            {
                if (data != null)
                {
                    data.Clear();
                }
            }
        }

        /// <summary>
        /// Determines whether the buffer contains an element with a specific value. 
        /// </summary>
        /// <param name="item">
        /// The object to locate in the buffer.
        /// </param>
        /// <returns>
        /// true if the element value is found in the buffer; otherwise false. 
        /// </returns>
        public bool Contains(T item)
        {
            lock (this.syncObject)
            {
                if (serializeInput)
                {
                    item = (T)(Object)GetSerializedObject(item);
                }

                return data.Contains(item);
            }
        }

        /// <summary>
        /// Copies the elements of the buffer to a specified array, starting at a particular index.
        /// </summary>
        /// <param name="array">
        /// The destination Array for the elements of type T copied from the buffer.
        /// </param>
        /// <param name="arrayIndex">
        /// The zero-based index in the array at which copying begins.
        /// </param>
        /// <exception cref="ArgumentException">
        /// array is multidimensional. 
        /// (or)
        /// arrayIndex is equal to or greater than the length of array. 
        /// (or)
        /// The number of elements in the source buffer is greater than the 
        /// available space from arrayIndex to the end of the destination array.
        /// (or)
        /// Type T cannot be cast automatically to the type of the destination array.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// array is a null reference
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// arrayIndex is less than 0.
        /// </exception>
        public void CopyTo(T[] array, int arrayIndex)
        {
            lock (syncObject)
            {
                data.CopyTo(array, arrayIndex);
            }
        }

        /// <summary>
        /// Removes the first occurrence of a specified item from the buffer. 
        /// </summary>
        /// <param name="item">
        /// The object to remove from the buffer.
        /// </param>
        /// <returns>
        /// true if item was successfully removed from the buffer; otherwise, false. 
        /// </returns>
        public bool Remove(T item)
        {
            lock (syncObject)
            {
                int index = InternalIndexOf(item);
                if (index < 0)
                {
                    return false;
                }

                RemoveItem(index);
                return true;
            }
        }

        #endregion

        #region IEnumerable Generic Overrides

        /// <summary>
        /// Returns an enumerator that iterates through the 
        /// elements of the buffer.
        /// </summary>
        /// <returns>
        /// An IEnumerator for objects of the type stored in the buffer.
        /// </returns>
        public IEnumerator<T> GetEnumerator()
        {
            return new PSDataCollectionEnumerator<T>(this, EnumeratorNeverBlocks);
        }

        #endregion

        #region IList Overrides

        /// <summary>
        /// Adds an element to the buffer.
        /// </summary>
        /// <param name="value">
        /// The object to add to the buffer.
        /// </param>
        /// <returns>
        /// The position into which the new element was inserted. 
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Objects cannot be added to a closed buffer. 
        /// Make sure the buffer is open for Add and Insert 
        /// operations to succeed.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// value reference is null.
        /// (or)
        /// value is not of the correct generic type T for the buffer.
        /// </exception>
        int IList.Add(object value)
        {
            PSDataCollection<T>.VerifyValueType(value);
            int index = data.Count;
            InternalAdd(Guid.Empty, (T)value);
            RaiseEvents(Guid.Empty, index);

            return index;            
        }

        /// <summary>
        /// Determines whether the collection contains an 
        /// element with a specific value. 
        /// </summary>
        /// <param name="value">
        /// The object to locate in the collection
        /// </param>
        /// <returns>
        /// true if the element value is found in the collection; 
        /// otherwise false.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// value reference is null.
        /// (or)
        /// value is not of the correct generic type T for the buffer.
        /// </exception>
        bool IList.Contains(object value)
        {
            PSDataCollection<T>.VerifyValueType(value);
            return Contains((T)value);
        }

        /// <summary>
        /// Determines the zero-based index of an element in the buffer. 
        /// </summary>
        /// <param name="value">
        /// The element in the buffer whose index is being determined.
        /// </param>
        /// <returns>
        /// The index of the value if found in the buffer; otherwise, -1. 
        /// </returns>
        /// <exception cref="ArgumentException">
        /// value reference is null.
        /// (or)
        /// value is not of the correct generic type T for the buffer.
        /// </exception>
        int IList.IndexOf(object value)
        {
            PSDataCollection<T>.VerifyValueType(value);
            return IndexOf((T)value);
        }

        /// <summary>
        /// Inserts an object into the buffer at a specified index. 
        /// </summary>
        /// <param name="index">
        /// The zero-based index at which value is to be inserted.
        /// </param>
        /// <param name="value">
        /// The object to insert into the buffer.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// index is not a valid index in the buffer.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// value reference is null.
        /// (or)
        /// value is not of the correct generic type T for the buffer.
        /// </exception>
        void IList.Insert(int index, object value)
        {
            PSDataCollection<T>.VerifyValueType(value);
            Insert(index, (T)value);
        }

        /// <summary>
        /// Removes the first occurrence of a specified object
        /// as an element from the buffer. 
        /// </summary>
        /// <param name="value">
        /// The object to be removed from the buffer.
        /// </param>
        /// <exception cref="ArgumentException">
        /// value reference is null.
        /// (or)
        /// value is not of the correct generic type T for the buffer.
        /// </exception>
        void IList.Remove(object value)
        {
            PSDataCollection<T>.VerifyValueType(value);
            Remove((T)value);
        }

        /// <summary>
        /// Gets a value that indicates whether the buffer is fixed in size.
        /// </summary>
        bool IList.IsFixedSize
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Gets a value that indicates whether the buffer is read-only.
        /// </summary>
        bool IList.IsReadOnly
        {
            get
            {
                return false;
            }
        }

        /// <summary>
        /// Gets or sets the element at the specified index. 
        /// </summary>
        /// <param name="index">
        /// The zero-based index of the element to get or set.
        /// </param>
        /// <exception cref="IndexOutOfRangeException">
        /// index is less than 0.
        /// (or)
        /// index is equal to or greater than Count.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// value reference is null.
        /// (or)
        /// value is not of the correct generic type T for the buffer.
        /// </exception>
        object IList.this[int index]
        {
            get
            {
                return this[index];
            }
            set
            {
                PSDataCollection<T>.VerifyValueType(value);
                this[index] = (T)value;
            }
        }

        #endregion

        #region ICollection Overrides

        /// <summary>
        /// Gets a value that indicates whether the buffer is synchronized.
        /// </summary>
        bool ICollection.IsSynchronized
        {
            get
            {
                return true;
            }
        }

        /// <summary>
        /// Gets the object used to synchronize access to the thread-safe buffer
        /// </summary>
        object ICollection.SyncRoot
        {
            get
            {
                return syncObject;
            }
        }

        /// <summary>
        /// Copies the elements of the collection to a specified array, 
        /// starting at a particular index. 
        /// </summary>
        /// <param name="array">
        /// The destination Array for the elements of type T copied
        /// from the buffer.
        /// </param>
        /// <param name="index">
        /// The zero-based index in the array at which copying begins.
        /// </param>
        /// <exception cref="ArgumentException">
        /// array is multidimensional. 
        /// (or)
        /// arrayIndex is equal to or greater than the length of array. 
        /// (or)
        /// The number of elements in the source buffer is greater than the 
        /// available space from arrayIndex to the end of the destination array.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// array is a null reference
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// arrayIndex is less than 0.
        /// </exception>
        void ICollection.CopyTo(Array array, int index)
        {
            lock (this.syncObject)
            {
                data.CopyTo((T[])array, index);
            }
        }

        #endregion

        #region IEnumerable Overrides

        /// <summary>
        /// Returns an enumerator that iterates through the buffer.
        /// </summary>
        /// <returns>
        /// An IEnumerator for objects of the type stored in the buffer.
        /// </returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return new PSDataCollectionEnumerator<T>(this, EnumeratorNeverBlocks);
        }

        #endregion

        #region Streaming Behavior

        /// <summary>
        /// Makes a shallow copy of all the elements currently in this collection
        /// and clears them from this collection. This will not result in a blocking call.
        /// 
        /// Calling this method might have side effects on the enumerator. When this
        /// method is called, the behavior of the enumerator is not defined.
        /// </summary>
        /// <returns>
        /// A new collection with a copy of all the elements in the current collection.
        /// </returns>
        public Collection<T> ReadAll()
        {
            return ReadAndRemove(0);
        }

        /// <summary>
        /// Makes a shallow copy of all the elements currently in this collection
        /// and clears them from this collection. This will not result in a blocking call.
        /// 
        /// Calling this method might have side effects on the enumerator. When this
        /// method is called, the behavior of the enumerator is not defined.
        /// </summary>
        /// <returns>
        /// A new collection with a copy of all the elements in the current collection.
        /// </returns>
        /// <param name="readCount">maximum number of elements to read</param>
        internal Collection<T> ReadAndRemove(int readCount)
        {
            Dbg.Assert(null != data, "Collection cannot be null");

            Dbg.Assert(readCount >= 0, "ReadCount cannot be negative");

            int resolvedReadCount = (readCount > 0 ? readCount : Int32.MaxValue);

            lock (syncObject)
            {
                // Copy the elements into a new collection
                // and clear.
                Collection<T> result = new Collection<T>();

                for(int i = 0; i < resolvedReadCount; i++)
                {
                    if (data.Count > 0)
                    {
                        result.Add(data[0]);
                        data.RemoveAt(0);
                    }
                    else
                    {
                        break;
                    }
                }

                if (null != readWaitHandle)
                {
                    if (data.Count > 0 || !isOpen)
                    {
                        // release all the waiting threads.
                        readWaitHandle.Set();
                    }
                    else
                    {
                        // reset the handle so that future 
                        // threads will block
                        readWaitHandle.Reset();
                    }
                }

                return result;
            }
        }

        internal T ReadAndRemoveAt0()
        {
            T value = default(T);

            lock (syncObject)
            {
                if (data != null && data.Count > 0)
                {
                    value = data[0];
                    data.RemoveAt(0);
                }
            }

            return value;
        }

        #endregion

        #region Protected Virtual Methods

        /// <summary>
        /// Inserts an item into the buffer at a specified index. 
        /// </summary>
        /// <param name="psInstanceId">
        /// InstanceId of PowerShell instance adding this data.
        /// Guid.Empty if not initiated by a PowerShell instance.
        /// </param>
        /// <param name="index">
        /// The zero-based index of the buffer where the object is to be inserted
        /// </param>
        /// <param name="item">
        /// The object to be inserted into the buffer.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The index specified is less than zero or greater 
        /// than Count.
        /// </exception>
        [SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "ps", Justification = "PS signifies PowerShell and is used at many places in the product.")]
        protected virtual void InsertItem(Guid psInstanceId, int index, T item)
        {
            RaiseDataAddingEvent(psInstanceId, item);

            if (serializeInput)
            {
                item = (T)(Object)GetSerializedObject(item);
            }

            data.Insert(index, item);
        }

        /// <summary>
        /// Removes the item at a specified index
        /// </summary>
        /// <param name="index">
        /// The zero-based index of the buffer where the object is to be removed.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The index specified is less than zero or greater 
        /// than the number of items in the buffer.
        /// </exception>
        protected virtual void RemoveItem(int index)
        {
            data.RemoveAt(index);
        }

        #endregion

        #region Serializable

        /// <summary>
        /// Implements the ISerializable contract for serializing a PSDataCollection
        /// </summary>
        /// <param name="info">Serialization information for this instance</param>
        /// <param name="context">The streaming context for this instance</param>
        public virtual void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null)
            {
                throw PSTraceSource.NewArgumentNullException("info");
            }

            info.AddValue("Data", data);
            info.AddValue("BlockingEnumerator", _blockingEnumerator);
            info.AddValue("DataAddedCount", _dataAddedFrequency);
            info.AddValue("EnumeratorNeverBlocks", EnumeratorNeverBlocks);
            info.AddValue("IsOpen", isOpen);
        }

        #endregion

        #region Internal/Private Methods and Properties

        /// <summary>
        /// Waitable handle for caller's to block until new data
        /// is added to the underlying buffer
        /// </summary>
        internal WaitHandle WaitHandle
        {
            get
            {
                if (null == readWaitHandle)
                {
                    lock (syncObject)
                    {
                        if (null == readWaitHandle)
                        {
                            // Create the handle signaled if there are objects in the buffer 
                            // or the buffer has been closed.
                            readWaitHandle = new ManualResetEvent(data.Count > 0 || !isOpen);
                        }
                    }
                }
                return readWaitHandle;
            }
        }       

        /// <summary>
        /// Utility method to signal handles and raise events
        /// in the consistent order.
        /// </summary>
        /// <param name="psInstanceId">
        /// PowerShell InstanceId which added this data.
        /// Guid.Empty, if the data is not added by a PowerShell
        /// instance.
        /// </param>
        /// <param name="index">
        /// Index at which the data is added.
        /// </param>
        private void RaiseEvents(Guid psInstanceId, int index)
        {
            bool raiseDataAdded = false;
            lock (syncObject)
            {
                if (null != readWaitHandle)
                {
                    // TODO: Should ObjectDisposedException be caught.

                    if (data.Count > 0 || !isOpen)
                    {
                        // release all the waiting threads.
                        readWaitHandle.Set();
                    }
                    else
                    {
                        // reset the handle so that future 
                        // threads will block
                        readWaitHandle.Reset();
                    }
                }
                // release any threads to notify an event. Enumertor
                // blocks on this syncObject.
                Monitor.PulseAll(syncObject);

                _countNewData++;
                if (_countNewData >= _dataAddedFrequency || (_countNewData > 0 &&!isOpen))
                {
                    raiseDataAdded = true;
                    _countNewData = 0;
                }
                else
                {
                    // store information in case _dataAddedFrequency is updated or collection completes
                    // so that event may be raised using last added data.
                    _lastPsInstanceId = psInstanceId;
                    _lastIndex = index;                    
                }
            }

            if (raiseDataAdded)
            {
                // We should raise the event outside of the lock
                // as the call is made into 3rd party code.
                RaiseDataAddedEvent(psInstanceId, index);
            }
        }

        private Guid _lastPsInstanceId;
        private int _lastIndex;

        private void RaiseDataAddingEvent(Guid psInstanceId, Object itemAdded)
        {
            // A temporary variable is used as the DataAdding may
            // reach null (because of -='s) after the null check
            EventHandler<DataAddingEventArgs> tempDataAdding = DataAdding;
            if (null != tempDataAdding)
            {
                tempDataAdding(this, new DataAddingEventArgs(psInstanceId, itemAdded));
            }
        }

        private void RaiseDataAddedEvent(Guid psInstanceId, int index)
        {
            // A temporary variable is used as the DataAdded may
            // reach null (because of -='s) after the null check
            EventHandler<DataAddedEventArgs> tempDataAdded = DataAdded;
            if (null != tempDataAdded)
            {
                tempDataAdded(this, new DataAddedEventArgs(psInstanceId, index));
            }   
        }

        /// <summary>
        /// Inserts an item into the buffer at a specified index. 
        /// The caller should make sure the method call is
        /// synchronized.
        /// </summary>
        /// <param name="psInstanceId">
        /// InstanceId of PowerShell instance adding this data.
        /// Guid.Empty if this is not initiated by a PowerShell instance.
        /// </param>
        /// <param name="index">
        /// The zero-based index of the buffer where the object is
        /// to be inserted.
        /// </param>
        /// <param name="item">
        /// The object to be inserted into the buffer.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Objects cannot be added to a closed buffer. 
        /// Make sure the buffer is open for Add and Insert 
        /// operations to succeed.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The index specified is less than zero or greater 
        /// than Count.
        /// </exception>
        private void InternalInsertItem(Guid psInstanceId, int index, T item)
        {
            if (!isOpen)
            {
                throw PSTraceSource.NewInvalidOperationException(PSDataBufferStrings.WriteToClosedBuffer);
            }

            InsertItem(psInstanceId, index, item);
        }

        /// <summary>
        /// Adds an item to the thread-safe buffer
        /// </summary>
        /// <param name="psInstanceId">
        /// InstanceId of PowerShell instance adding this data.
        /// Guid.Empty if this is not initiated by a PowerShell instance.
        /// </param>
        /// <param name="item">
        /// item to add
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Objects cannot be added to a closed buffer. 
        /// Make sure the buffer is open for Add and Insert 
        /// operations to succeed.
        /// </exception>
        internal void InternalAdd(Guid psInstanceId, T item)
        {
            // should not rely on data.Count in "finally"
            // as another thread might add data
            int index = -1;

            lock (syncObject)
            {
                // Add the item and set to raise events
                // so that events are raised outside of
                // lock.
                index = data.Count;
                InternalInsertItem(psInstanceId, index, item);
            }

            if (index > -1)
            {
                RaiseEvents(psInstanceId, index);
            }
        }

        /// <summary>
        /// Adds the elements of an ICollection to the end of the buffer. 
        /// </summary>
        /// <param name="psInstanceId">
        /// InstanceId of PowerShell instance adding this data.
        /// </param>
        /// <param name="collection">
        /// The ICollection whose elements should be added to the end of 
        /// the buffer.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="collection"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// value is not of the correct generic type T for the buffer.
        /// </exception>
        internal void InternalAddRange(Guid psInstanceId, ICollection collection)
        {
            if (null == collection)
            {
                throw PSTraceSource.NewArgumentNullException("collection");
            }

            int index = -1;
            bool raiseEvents = false;

            lock (syncObject)
            {
                if (!isOpen)
                {
                    throw PSTraceSource.NewInvalidOperationException(PSDataBufferStrings.WriteToClosedBuffer);
                }

                index = data.Count;

                foreach (object o in collection)
                {
                    InsertItem(psInstanceId, data.Count, (T)o);

                    // set raise events if atlease one item is
                    // added.
                    raiseEvents = true;
                }
            }

            if (raiseEvents)
            {
                RaiseEvents(psInstanceId, index);
            }
        }

        /// <summary>
        /// Increment counter to keep track of active PowerShell instances
        /// using this buffer. This is used only internally.
        /// </summary>
        internal void AddRef()
        {
            lock (syncObject)
            {
                refCount++;
            }
        }

        /// <summary>
        /// Decrement counter to keep track of active PowerShell instances
        /// using this buffer. This is used only internally. 
        /// </summary>
        internal void DecrementRef()
        {
            lock (syncObject)
            {
                Dbg.Assert(refCount > 0, "RefCount cannot be <= 0");

                refCount--;
                if (refCount != 0 && (!_blockingEnumerator || refCount != 1)) return;

                // release threads blocked on waithandle
                if (null != readWaitHandle)
                {
                    readWaitHandle.Set();
                }

                // release any threads to notify refCount is 0. Enumertor
                // blocks on this syncObject and it needs to be notified
                // when the count becomes 0. 
                Monitor.PulseAll(syncObject);
            }
        }

        /// <summary>
        /// Returns the index of first occurece of <paramref name="item"/>
        /// in the buffer. 
        /// This method is not thread safe.
        /// </summary>
        /// <param name="item">
        /// The object to locate in the buffer.
        /// </param>
        /// <returns>
        /// 0 based index of item if found,
        /// -1 otherwise.
        /// </returns>
        private int InternalIndexOf(T item)
        {
            if (serializeInput)
            {
                item = (T)(Object)GetSerializedObject(item);
            }

            int count = data.Count;
            for (int index = 0; index < count; index++)
            {
                if (object.Equals(data[index], item))
                {
                    return index;
                }
            }
            return -1;
        }

        /// <summary>
        /// Checks if the <paramref name="value"/> is of type T.
        /// </summary>
        /// <param name="value">
        /// Value to verify.
        /// </param>
        /// <exception cref="ArgumentException">
        /// value reference is null.
        /// (or)
        /// value is not of the correct generic type T for the buffer.
        /// </exception>
        private static void VerifyValueType(object value)
        {
            if (null == value)
            {
                if (typeof(T).GetTypeInfo().IsValueType)
                {
                    throw PSTraceSource.NewArgumentNullException("value", PSDataBufferStrings.ValueNullReference);
                }
            }
            else if (!(value is T))
            {
                throw PSTraceSource.NewArgumentException("value", PSDataBufferStrings.CannotConvertToGenericType,
                                                         value.GetType().FullName,
                                                         typeof (T).FullName);
            }
        }

        // Serializes an object, as long as it's not serialized.
        private PSObject GetSerializedObject(Object value)
        {
            // This is a safe cast, as this method is only called with "SerializeInput" is set,
            // and that method throws if the collection type is not PSObject.
            PSObject result = value as PSObject;

            // Check if serialization would be idempotent
            if (SerializationWouldHaveNoEffect(result))
            {
                return result;
            }
            else
            {
                Object deserialized = PSSerializer.Deserialize(PSSerializer.Serialize(value));
                if(deserialized == null)
                {
                    return (PSObject) deserialized;
                }
                else
                {
                    return PSObject.AsPSObject(deserialized);
                }
            }
        }

        private bool SerializationWouldHaveNoEffect(PSObject result)
        {
            if(result == null)
            {
                return true;
            }

            Object baseObject = PSObject.Base(result);
            if(baseObject == null)
            {
                return true;
            }

            // Check if it's a primitive known type
            if(InternalSerializer.IsPrimitiveKnownType(baseObject.GetType()))
            {
                return true;
            }

            // Check if it's a CIM type
            if(baseObject is Microsoft.Management.Infrastructure.CimInstance)
            {
                return true;
            }
        
            // Check if it's got "Deserialized" in its type name
            if(result.TypeNames[0].StartsWith("Deserialized", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Sync object for this collection
        /// </summary>
        internal Object SyncObject
        {
            get
            {
                return syncObject;
            }
        }

        /// <summary>
        /// Reference count variable
        /// </summary>
        internal Int32 RefCount
        {
            get
            {
                return refCount;
            }
            set
            {
                lock (syncObject)
                {
                    refCount = value;
                }
            }
        }

        #endregion

        #region Idle event

        /// <summary>
        /// Indicates whether or not the collection should pulse idle events
        /// </summary>
        internal bool PulseIdleEvent
        {
            get { return (IdleEvent != null); }
        }

        internal event EventHandler<EventArgs> IdleEvent;

        /// <summary>
        /// Fires an idle event
        /// </summary>
        internal void FireIdleEvent()
        {
            IdleEvent.SafeInvoke(this, null);
        }

        /// <summary>
        /// Pulses the collection
        /// </summary>
        internal void Pulse()
        {
            lock (syncObject)
            {
                Monitor.PulseAll(syncObject);
            }
        }

        #endregion

        #region IDisposable Overrides

        /// <summary>
        /// Public dispose method
        /// </summary>
        public void Dispose()
        {
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Release all the resources
        /// </summary>
        /// <param name="disposing">if true, release all managed resources</param>
        protected void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (isDisposed)
                {
                    return;
                }

                lock (syncObject)
                {
                    if (isDisposed)
                    {
                        return;
                    }
                    isDisposed = true;
                }

                Complete();

                lock (this.syncObject)
                {
                    if (readWaitHandle != null)
                    {
                        readWaitHandle.Dispose();
                        readWaitHandle = null;
                    }

                    if (data != null)
                    {
                        data.Clear();
                    }
                }
            }
        }
        #endregion IDisposable Overrides
    }

    #endregion

    /// <summary>
    /// Interface to support PSDataCollectionEnumerator.
    /// Needed to provide a way to get to the non-blocking
    /// MoveNext implementation.
    /// </summary>
    /// <typeparam name="W"></typeparam>
    internal interface IBlockingEnumerator<out W> : IEnumerator<W>
    {
        bool MoveNext(bool block);
    }

    #region PSDataCollectionEnumerator

    /// <summary>
    /// Enumerator for PSDataCollection. This enumerator blocks until 
    /// either all the PowerShell operations are compeleted or the
    /// PSDataCollection is closed.
    /// </summary>
    /// <typeparam name="W"></typeparam>
    internal sealed class PSDataCollectionEnumerator<W> : IBlockingEnumerator<W>
    {
        #region Private Data

        private W currentElement;
        private int index;
        private PSDataCollection<W> collToEnumerate;
        private bool _neverBlock;

        #endregion

        #region Constructor

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="collection">
        /// PSDataCollection to enumerate.
        /// </param>
        /// <param name="neverBlock">
        /// Controls if the enumerator is blocking by default or not.
        /// </param>
        internal PSDataCollectionEnumerator(PSDataCollection<W> collection, bool neverBlock)
        {
            Dbg.Assert(null != collection, 
                "Collection cannot be null");
            Dbg.Assert(!collection.ReleaseOnEnumeration || !collection.IsEnumerated, 
                "shouldn't enumerate more than once if ReleaseOnEnumeration is true");

            collToEnumerate = collection;
            index = 0;
            currentElement = default(W);
            collToEnumerate.IsEnumerated = true;
            _neverBlock = neverBlock;
        }

        #endregion

        #region IEnumerator Overrides

        /// <summary>
        /// Gets the element in the collection at the current position 
        /// of the enumerator.
        /// </summary>
        /// <remarks>
        /// For better performance, this property does not throw an exception
        /// if the enumerator is positioned before the first element or after
        /// the last element; the value of the property is undefined.
        /// </remarks>
        W IEnumerator<W>.Current
        {
            get
            {
                return currentElement;
            }
        }

        /// <summary>
        /// Gets the element in the collection at the current position 
        /// of the enumerator.
        /// </summary>
        /// <remarks>
        /// For better performance, this property does not throw an exception
        /// if the enumerator is positioned before the first element or after
        /// the last element; the value of the property is undefined.
        /// </remarks>
        public object Current
        {
            get
            {
                return currentElement;
            }
        }

        /// <summary>
        /// Advances the enumerator to the next element in the collection.
        /// </summary>
        /// <returns>
        /// true if the enumerator successfully advanced to the next element; 
        /// otherwise, false.
        /// </returns>
        /// <remarks>
        /// This will block if the original collection is attached to any 
        /// active PowerShell instances and the original collection is not
        /// closed.
        /// </remarks>
        public bool MoveNext()
        {
            return MoveNext(_neverBlock == false);
        }

        /// <summary>
        /// Advances the enumerator to the next element in the collection.
        /// </summary>
        /// <returns>
        /// true if the enumerator successfully advanced to the next element; 
        /// otherwise, false.
        /// </returns>
        /// <param name="block">true - to block when no elements are available</param>
        public bool MoveNext(bool block)
        {
            lock (collToEnumerate.SyncObject)
            {
                do
                {
                    if (index < collToEnumerate.Count)
                    {
                        currentElement = collToEnumerate[index];
                        if (collToEnumerate.ReleaseOnEnumeration)
                        {
                            collToEnumerate[index] = default(W);
                        }
                        index++;
                        return true;
                    }

                    // we have reached the end if either the collection is closed
                    // or no powershell instance is bound to this collection.
                    if ((0 == collToEnumerate.RefCount) || (!collToEnumerate.IsOpen))
                    {
                        return false;
                    }

                    if (block)
                    {
                        if (collToEnumerate.PulseIdleEvent)
                        {
                            collToEnumerate.FireIdleEvent();
                            Monitor.Wait(collToEnumerate.SyncObject);
                        }
                        else
                        {
                            // using light-weight monitor to block the current thread instead
                            // of AutoResetEvent. This saves using Kernel objects.
                            Monitor.Wait(collToEnumerate.SyncObject);
                        }
                    }
                    else
                    {
                        return false;
                    }
                } while (true);
            }
        }

        /// <summary>
        /// Resets the enumerator to its initial position, 
        /// which is before the first element in the collection. 
        /// </summary>
        public void Reset()
        {
            currentElement = default(W);
            index = 0;
        }

        /// <summary>
        /// 
        /// </summary>
        void IDisposable.Dispose()
        {
        }

        #endregion
    }

    #endregion

    /// <summary>
    /// Class that represents various informational buffers like 
    /// verbose, debug, warning, progress, information used with command invocation. 
    /// </summary>
    internal sealed class PSInformationalBuffers
    {
        private Guid psInstanceId;

        /// <summary>
        /// Default constructor.
        /// </summary>
        /// <param name="psInstanceId">
        /// Guid of Powershell instance creating this buffers.
        /// Whenver an item is added to one of the buffers, this id is
        /// used to notify the buffer about the PowerShell instance adding
        /// this data.
        /// </param>
        internal PSInformationalBuffers(Guid psInstanceId)
        {
            Dbg.Assert(psInstanceId != Guid.Empty,
                "PowerShell instance id cannot be Guid.Empty");

            this.psInstanceId = psInstanceId;
            progress = new PSDataCollection<ProgressRecord>();
            verbose = new PSDataCollection<VerboseRecord>();
            debug = new PSDataCollection<DebugRecord>();
            warning = new PSDataCollection<WarningRecord>();
            information = new PSDataCollection<InformationRecord>();
        }

        #region Internal Methods / Properties

        /// <summary>
        /// A buffer representing Progress record objects of a PowerShell command invocation.
        /// Can be null.
        /// </summary>
        internal PSDataCollection<ProgressRecord> Progress
        {
            get { return progress; }
            set
            {
                progress = value;
            }
        }
        internal PSDataCollection<ProgressRecord> progress;

        /// <summary>
        /// A buffer representing Verbose objects of a PowerShell command invocation.
        /// Can be null.
        /// </summary>
        internal PSDataCollection<VerboseRecord> Verbose
        {
            get { return verbose; }
            set
            {
                verbose = value;
            }
        }
        internal PSDataCollection<VerboseRecord> verbose;

        /// <summary>
        /// A buffer representing Debug objects of a PowerShell command invocation.
        /// Can be null.
        /// </summary>
        internal PSDataCollection<DebugRecord> Debug
        {
            get { return debug; }
            set
            {
                debug = value;
            }
        }
        internal PSDataCollection<DebugRecord> debug;

        /// <summary>
        /// A buffer representing Warning objects of a PowerShell command invocation.
        /// Can be null.
        /// </summary>
        internal PSDataCollection<WarningRecord> Warning
        {
            get { return warning; }
            set
            {
                warning = value;
            }
        }
        private PSDataCollection<WarningRecord> warning;

        /// <summary>
        /// A buffer representing Information objects of a PowerShell command invocation.
        /// Can be null.
        /// </summary>
        internal PSDataCollection<InformationRecord> Information
        {
            get { return information; }
            set
            {
                information = value;
            }
        }
        private PSDataCollection<InformationRecord> information;

        /// <summary>
        /// Adds item to the progress buffer.
        /// The item is added to the buffer along with PowerShell InstanceId.
        /// </summary>
        /// <param name="item"></param>
        internal void AddProgress(ProgressRecord item)
        {
            if (null != progress)
            {
                progress.InternalAdd(psInstanceId, item);
            }
        }

        /// <summary>
        /// Adds item to the verbose buffer.
        /// The item is added to the buffer along with PowerShell InstanceId.
        /// </summary>
        /// <param name="item"></param>
        internal void AddVerbose(VerboseRecord item)
        {
            if (null != verbose)
            {
                verbose.InternalAdd(psInstanceId, item);            
            }
        }

        /// <summary>
        /// Adds item to the debug buffer.
        /// The item is added to the buffer along with PowerShell InstanceId.
        /// </summary>
        /// <param name="item"></param>
        internal void AddDebug(DebugRecord item)
        {
            if (null != debug)
            {
                debug.InternalAdd(psInstanceId, item);
            }
        }

        /// <summary>
        /// Adds item to the warning buffer.
        /// The item is added to the buffer along with PowerShell InstanceId.
        /// </summary>
        /// <param name="item"></param>
        internal void AddWarning(WarningRecord item)
        {
            if (null != warning)
            {
                warning.InternalAdd(psInstanceId, item);
            }
        }

        /// <summary>
        /// Adds item to the information buffer.
        /// The item is added to the buffer along with PowerShell InstanceId.
        /// </summary>
        /// <param name="item"></param>
        internal void AddInformation(InformationRecord item)
        {
            if (null != information)
            {
                information.InternalAdd(psInstanceId, item);
            }
        }

        #endregion
    }
}