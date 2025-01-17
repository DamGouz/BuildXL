// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Utilities.Tasks;

namespace BuildXL.Utilities.ParallelAlgorithms
{
    /// <summary>
    /// An exception is thrown when the <see cref="ActionBlockSlim{T}"/> is full and can't accept new items.
    /// </summary>
    public sealed class ActionBlockIsFullException : InvalidOperationException
    {
        /// <nodoc />
        public int ConcurrencyLimit { get; }

        /// <nodoc />
        public int CurrentCount { get; }

        /// <nodoc />
        public ActionBlockIsFullException(string message, int concurrencyLimit, int currentCount)
            : base(message)
        {
            ConcurrencyLimit = concurrencyLimit;
            CurrentCount = currentCount;
        }
    }

    /// <summary>
    /// Light-weight version of a non-dataflow block that invokes a provided <see cref="Action{T}"/> delegate for every data element received in parallel with limited concurrency.
    /// </summary>
    public sealed class ActionBlockSlim<T>
    {
        private readonly Func<T, Task> m_processItemAction;
        private readonly ConcurrentQueue<T> m_queue;

        private bool m_schedulingCompleted;

        private int m_pending;
        
        // TODO ST: Add tests to check the capacity limiting logic.
        private readonly int? m_capacityLimit;

        private readonly SemaphoreSlim m_semaphore;

        private readonly List<Task> m_tasks;

        /// <summary>
        /// Task completion source used for signaling that <see cref="Complete"/> method was called.
        /// </summary>
        private readonly TaskSourceSlim<object> m_schedulingCompletedTcs = TaskSourceSlim.Create<object>();

        /// <summary>
        /// Returns the number of pending items.
        /// </summary>
        public int PendingWorkItems => m_pending;

        /// <summary>
        /// Creates an instance of the action block.
        /// </summary>
        /// <remarks>
        /// Please use this constructor only for CPU intensive (non-asynchronous) callbacks.
        /// If you need to control the concurrency for asynchronous operations, please use <see cref="CreateWithAsyncAction"/> helper.
        /// </remarks>
        public ActionBlockSlim(int degreeOfParallelism, Action<T> processItemAction, int? capacityLimit = null)
            : this(degreeOfParallelism, t =>
            {
                processItemAction(t);
                return Task.CompletedTask;
            }, capacityLimit: capacityLimit)
        {
        }

        /// <nodoc />
        public static ActionBlockSlim<T> CreateWithAsyncAction(int degreeOfParallelism, Func<T, Task> processItemAction, int? capacityLimit = null)
        {
            return new ActionBlockSlim<T>(degreeOfParallelism, processItemAction, capacityLimit);
        }

        /// <nodoc />
        private ActionBlockSlim(int degreeOfParallelism, Func<T, Task> processItemAction, int? capacityLimit = null)
        {
            Contract.Requires(degreeOfParallelism >= -1);
            Contract.RequiresNotNull(processItemAction);

            m_processItemAction = processItemAction;
            m_capacityLimit = capacityLimit;
            degreeOfParallelism = degreeOfParallelism == -1 ? Environment.ProcessorCount : degreeOfParallelism;
            
            m_queue = new ConcurrentQueue<T>();

            // Semaphore count is 0 to ensure that all the tasks are blocked unless new data is scheduled.
            m_semaphore = new SemaphoreSlim(0, int.MaxValue);

            m_tasks = new List<Task>(degreeOfParallelism);
            for (int i = 0; i < degreeOfParallelism; i++)
            {
                m_tasks.Add(CreateProcessorItemTask(degreeOfParallelism));
            }

            DegreeOfParallelism = degreeOfParallelism;
        }

        /// <summary>
        /// Add a given <paramref name="item"/> to a processing queue.
        /// </summary>
        /// <exception cref="ActionBlockIsFullException">If the queue is full and the queue was configured to limit the queue size.</exception>
        public void Post(T item)
        {
            AssertNotCompleted();

            var currentCount = Interlocked.Increment(ref m_pending);
            if (m_capacityLimit != null && currentCount > m_capacityLimit.Value)
            {
                Interlocked.Decrement(ref m_pending);

                throw new ActionBlockIsFullException(
                    $"Can't add new item because the queue is full. Capacity is '{m_capacityLimit.Value}'. CurrentCount is '{currentCount}'.", m_capacityLimit.Value, currentCount);
            }

            // NOTE: Enqueue MUST happen before releasing the semaphore
            // to ensure WaitAsync below never returns when there is not
            // a corresponding item in the queue to be dequeued. The only
            // exception is on completion of all items.
            m_queue.Enqueue(item);
            m_semaphore.Release();
        }

        /// <summary>
        /// Marks the action block as completed.
        /// </summary>
        public void Complete()
        {
            bool schedulingCompleted = Volatile.Read(ref m_schedulingCompleted);
            if (schedulingCompleted)
            {
                return;
            }

            Volatile.Write(ref m_schedulingCompleted, true);
            
            // Release one thread that will release all the threads when all the elements are processed.
            m_semaphore.Release();
            m_schedulingCompletedTcs.SetResult(null);
        }

        /// <summary>
        /// Returns a task that will be completed when <see cref="Complete"/> method is called and all the items added to the queue are processed.
        /// </summary>
        public async Task CompletionAsync()
        {
            // The number of processing task could be changed via IncreaseConcurrencyTo method calls,
            // so we need to make sure that Complete method was called by "awaiting" for the task completion source.
            await m_schedulingCompletedTcs.Task;

            await Task.WhenAll(m_tasks.ToArray());
        }

        /// <summary>
        /// Current degree of parallelism.
        /// </summary>
        public int DegreeOfParallelism { get; private set; }

        /// <summary>
        /// Increases the current concurrency level from <see cref="DegreeOfParallelism"/> to <paramref name="maxDegreeOfParallelism"/>.
        /// </summary>
        public void IncreaseConcurrencyTo(int maxDegreeOfParallelism)
        {
            Contract.Requires(maxDegreeOfParallelism > DegreeOfParallelism);
            AssertNotCompleted();

            var degreeOfParallelism = maxDegreeOfParallelism - DegreeOfParallelism;
            DegreeOfParallelism = maxDegreeOfParallelism;

            for (int i = 0; i < degreeOfParallelism; i++)
            {
                m_tasks.Add(CreateProcessorItemTask(degreeOfParallelism));
            }
        }

        private void AssertNotCompleted([CallerMemberName]string callerName = null)
        {
            bool schedulingCompleted = Volatile.Read(ref m_schedulingCompleted);
            Contract.Check(!schedulingCompleted)?.Assert($"Operation '{callerName}' is invalid because 'Complete' method was already called.");
        }

        private Task CreateProcessorItemTask(int degreeOfParallelism)
        {
            return Task.Run(
                async () =>
                {
                    while (true)
                    {
                        await m_semaphore.WaitAsync();

                        if (m_queue.TryDequeue(out var item))
                        {
                            await m_processItemAction(item);
                        }

                        // Could be -1 if the number of pending items is already 0 and the task was awakened for graceful finish.
                        if (Interlocked.Decrement(ref m_pending) <= 0 && Volatile.Read(ref m_schedulingCompleted))
                        {
                            // Ensure all tasks are unblocked and can gracefully
                            // finish since there are at most degreeOfParallelism - 1 tasks
                            // waiting at this point
                            m_semaphore.Release(degreeOfParallelism);
                            return;
                        }
                    }
                });
        }
    }
}
