﻿// Copyright (c) 2020 Orion Edwards
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Threading;

namespace Dispatch.SerialQueueTest
{
    [TestClass]
    public class SerialQueueTest_DispatchSync
    {
        [TestMethod]
        public void DispatchSyncRunsNormally()
        {
            var q = new SerialQueue(new InvalidThreadPool(), null, SerialQueueFeatures.None);
            var hit = new List<int>();
            q.DispatchSync(() => {
                hit.Add(1);
            });
            CollectionAssert.AreEqual(new[] { 1 }, hit);
        }

        // this is different to an Apple GCD queue. 
        // It works because our queue is internally using a C# lock, which is recursive.
        // On an idle queue, DispatchSync is more or less lock(queue) which allows recursion
        // due to the fact that lock(queue) is recursive in C#.
        [TestMethod]
        public void NestedDispatchSyncDoesntDeadlock()
        {
            var q = new SerialQueue(new InvalidThreadPool(), null, SerialQueueFeatures.None);
            var hit = new List<int>();
            q.DispatchSync(() => {
                hit.Add(1);
                q.DispatchSync(() => {
                    hit.Add(2);
                });
                hit.Add(3);
            });
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, hit);
        }

        // This is also different to an apple GCD queue. 
        // Because DispatchSync{ DispatchSync {} } doesn't deadlock (see above)
        // it would be really strange if DispatchAsync { DispatchSync {} }  DID deadlock, so
        // we take the performance hit. Could put this ability behind a Feature in the future if need be
        [TestMethod]
        public void NestedDispatchSyncInsideDispatchAsyncDoesntDeadlock()
        {
            var q = new SerialQueue(new TaskThreadPool(), null, SerialQueueFeatures.None);
            var hit = new List<int>();
            var mre = new ManualResetEvent(false);
            q.DispatchAsync(() => {
                hit.Add(1);
                q.DispatchSync(() => {
                    hit.Add(2);
                });
                hit.Add(3);
                mre.Set();
            });
            mre.WaitOne();
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, hit);
        }

        [TestMethod]
        public void DispatchSyncConcurrentWithAnotherDispatchAsyncWorksProperly()
        {
            var q = new SerialQueue(new TaskThreadPool(), null, SerialQueueFeatures.None);
            var hit = new List<int>();
            var are = new AutoResetEvent(false);
            q.DispatchAsync(() => {
                hit.Add(1);
                are.Set();
                Thread.Sleep(100); // we can't block on an event as that would deadlock, we just have to slow it down enough to force the DispatchSync to wait
                hit.Add(2);
            });
            are.WaitOne();
            q.DispatchSync(() => {
                hit.Add(3);
            });

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, hit);
        }
    }

    [TestClass]
    public class SerialQueueTest_DispatchAsync
    {
        readonly MockThreadPool mockPool = new MockThreadPool(); // mstest creates a new instance for each test so we don't need things like BeforeEach
        readonly TaskThreadPool realPool = new TaskThreadPool();

        [TestMethod]
        public void DispatchAsyncQueuesToThreadpool()
        {
            var q = new SerialQueue(mockPool, null, SerialQueueFeatures.None);
            var hit = new List<int>();
            q.DispatchAsync(() => {
                hit.Add(1);
            });
            CollectionAssert.AreEqual(new int[0], hit);

            mockPool.RunNextAction();

            CollectionAssert.AreEqual(new[] { 1 }, hit);
            Assert.AreEqual(0, mockPool.Actions.Count);
        }

        [TestMethod]
        public void MultipleDispatchAsyncCallsGetProcessedByOneWorker()
        {
            var q = new SerialQueue(mockPool, null, SerialQueueFeatures.None);
            var hit = new List<int>();
            q.DispatchAsync(() => {
                hit.Add(1);
            });
            q.DispatchAsync(() => {
                hit.Add(2);
            });
            CollectionAssert.AreEqual(new int[0], hit);

            mockPool.RunNextAction();

            CollectionAssert.AreEqual(new[] { 1, 2 }, hit);
            Assert.AreEqual(0, mockPool.Actions.Count);
        }

        [TestMethod]
        public void NestedDispatchAsyncCallsGetProcessedByOneWorker()
        {
            var q = new SerialQueue(mockPool, null, SerialQueueFeatures.None);
            var hit = new List<int>();
            q.DispatchAsync(() => {
                hit.Add(1);
                q.DispatchAsync(() => {
                    hit.Add(3);
                });
                hit.Add(2);
            });

            CollectionAssert.AreEqual(new int[0], hit);

            mockPool.RunNextAction();

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, hit);
            Assert.AreEqual(0, mockPool.Actions.Count);
        }

        [TestMethod]
        public void DispatchAsyncCanBeCanceled()
        {
            var q = new SerialQueue(mockPool, null, SerialQueueFeatures.None);
            var hit = new List<int>();
            var d = q.DispatchAsync(() => hit.Add(1));
            Assert.AreEqual(1, mockPool.Actions.Count);

            d.Dispose();
            Assert.AreEqual(1, mockPool.Actions.Count); // we can't "take it out" of the threadpool as not all threadpools support that

            mockPool.RunNextAction();
            CollectionAssert.AreEqual(new int[0], hit); // lambda didn't run
            Assert.AreEqual(0, mockPool.Actions.Count);
        }

        [TestMethod]
        public void DispatchAsyncCanBeSafelyCanceledAfterItsRun()
        {
            var q = new SerialQueue(mockPool, null, SerialQueueFeatures.None);
            var hit = new List<int>();

            var d = q.DispatchAsync(() => hit.Add(1));
            mockPool.RunNextAction();

            CollectionAssert.AreEqual(new int[]{ 1 }, hit);
            Assert.AreEqual(0, mockPool.Actions.Count);

            d.Dispose();

            CollectionAssert.AreEqual(new int[] { 1 }, hit); // lambda didn't run
            Assert.AreEqual(0, mockPool.Actions.Count);
        }

        [TestMethod]
        public void MultipleConcurrentDispatchAsyncsRunInSerialFollowedByDispatchSync()
        {
            var q = new SerialQueue(realPool, null, SerialQueueFeatures.None);
            var hit = new List<int>();
            q.DispatchAsync(() => {
                hit.Add(1);
                Thread.Sleep(50);
                hit.Add(2);
            });
            q.DispatchAsync(() => {
                hit.Add(3);
                Thread.Sleep(50);
                hit.Add(4);
            });
            q.DispatchSync(() => {
                hit.Add(5);
            });
            q.DispatchSync(() => {
                hit.Add(6);
            });
            q.DispatchAsync(() => {
                hit.Add(7);
                Thread.Sleep(50);
                hit.Add(8);
            });
            q.DispatchSync(() => {
                hit.Add(9);
            });

            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, hit);
        }
    }

    [TestClass]
    public class SerialQueueTest_DispatchAfter
    {
        readonly MockThreadPool mockPool = new MockThreadPool();
        readonly SerialQueue sq;

        public SerialQueueTest_DispatchAfter()
        {
            sq = new SerialQueue(mockPool, null, SerialQueueFeatures.None);
        }

        [TestMethod]
        public void DispatchAfterRunsFunctionAfterElapsedTime()
        {
            var hit = new List<int>();
            sq.DispatchAfter(TimeSpan.FromMilliseconds(100), () => hit.Add(1));

            Assert.AreEqual(0, hit.Count);
            Assert.AreEqual(0, mockPool.Actions.Count);
            Assert.AreEqual(1, mockPool.ScheduledActions.Count);

            mockPool.AdvanceClock(TimeSpan.FromMilliseconds(99));

            Assert.AreEqual(0, hit.Count);
            Assert.AreEqual(0, mockPool.Actions.Count);
            Assert.AreEqual(1, mockPool.ScheduledActions.Count);

            mockPool.AdvanceClock(TimeSpan.FromMilliseconds(2));

            Assert.AreEqual(0, hit.Count);
            Assert.AreEqual(1, mockPool.Actions.Count);
            Assert.AreEqual(0, mockPool.ScheduledActions.Count);

            mockPool.RunNextAction();

            CollectionAssert.AreEqual(new[] { 1 }, hit);
            Assert.AreEqual(0, mockPool.Actions.Count);
            Assert.AreEqual(0, mockPool.ScheduledActions.Count);
        }

        [TestMethod]
        public void CancellingDispatchAfterFunctionStopsItRunning()
        {
            var hit = new List<int>();
            var cancel = sq.DispatchAfter(TimeSpan.FromMilliseconds(100), () => hit.Add(1));

            Assert.AreEqual(0, hit.Count);
            Assert.AreEqual(0, mockPool.Actions.Count);
            Assert.AreEqual(1, mockPool.ScheduledActions.Count);

            mockPool.AdvanceClock(TimeSpan.FromMilliseconds(99));

            cancel.Dispose();

            Assert.AreEqual(0, hit.Count);
            Assert.AreEqual(0, mockPool.Actions.Count);
            Assert.AreEqual(0, mockPool.ScheduledActions.Count);

            mockPool.AdvanceClock(TimeSpan.FromMilliseconds(2));

            Assert.AreEqual(0, hit.Count);
            Assert.AreEqual(0, mockPool.Actions.Count);
            Assert.AreEqual(0, mockPool.ScheduledActions.Count);
        }
    }

    [TestClass]
    public class SerialQueueTest_Disposal
    {
        readonly InvalidThreadPool invalidPool = new InvalidThreadPool();

        [TestMethod]
        public void CantCallDispatchSyncOnDisposedQueue()
        {
            var sq = new SerialQueue(invalidPool, null, SerialQueueFeatures.None);
            sq.Dispose();
            var hit = new List<int>();

            AssertEx.Throws<ObjectDisposedException>(() => {
                sq.DispatchSync(() => hit.Add(1));
            });
        }

        [TestMethod]
        public void CantCallDispatchAsyncOnDisposedQueue()
        {
            var sq = new SerialQueue(invalidPool, null, SerialQueueFeatures.None);
            sq.Dispose();
            var hit = new List<int>();

            AssertEx.Throws<ObjectDisposedException>(() => {
                sq.DispatchAsync(() => hit.Add(1));
            });
        }

        [TestMethod]
        public void CantCallDispatchAfterOnDisposedQueue()
        {
            var sq = new SerialQueue(invalidPool, null, SerialQueueFeatures.None);
            sq.Dispose();
            var hit = new List<int>();

            AssertEx.Throws<ObjectDisposedException>(() => {
                sq.DispatchAfter(TimeSpan.FromMilliseconds(100), () => hit.Add(1));
            });
        }
    }

    [TestClass]
    public class SerialQueueTest_NameAndCurrent
    {
        readonly MockThreadPool mockPool = new MockThreadPool();
        readonly InvalidThreadPool invalidPool = new InvalidThreadPool();

        [TestMethod]
        public void CanSetAName()
        {
            var defq = new SerialQueue();
            Assert.IsNull(defq.Name);

            var sq = new SerialQueue(invalidPool, "q1");
            Assert.AreEqual("q1", sq.Name);
        }

        [TestMethod]
        public void CurrentQueueReturnsNullWhenNotOnQueue()
        {
            var sq = new SerialQueue(invalidPool, "q1");

            Assert.IsNull(SerialQueue.Current);
        }

        [TestMethod]
        public void CurrentQueueReturnsQueueInDispatchSync()
        {
            var sq = new SerialQueue(invalidPool, "q1");

            Assert.IsNull(SerialQueue.Current);

            sq.DispatchSync(() => {
                Assert.AreSame(sq, SerialQueue.Current);
            });

            Assert.IsNull(SerialQueue.Current);
        }

        [TestMethod]
        public void CurrentQueueReturnsTopmostQueueInDispatchSync()
        {
            var q1 = new SerialQueue(invalidPool, "q1");
            var q2 = new SerialQueue(invalidPool, "q2");

            Assert.IsNull(SerialQueue.Current);

            q1.DispatchSync(() => {
                q2.DispatchSync(() => {
                    Assert.AreSame(q2, SerialQueue.Current);
                });
            });

            Assert.IsNull(SerialQueue.Current);
        }

        [TestMethod]
        public void CurrentQueueReturnsQueueInDispatchAsync()
        {
            var sq = new SerialQueue(mockPool, "q1");

            Assert.IsNull(SerialQueue.Current);

            var mre = new ManualResetEvent(false);

            var captured = new List<SerialQueue>();
            sq.DispatchAsync(() => {
                captured.Add(SerialQueue.Current);
            });

            mockPool.RunNextAction();

            Assert.AreEqual(1, captured.Count);
            Assert.AreSame(captured[0], sq);

            Assert.IsNull(SerialQueue.Current);
        }
    }

    // Deliberately poisoned threadpool that will throw an exception if anyone tries to actually
    // use it. Used to sanity check sync behaviour
    class InvalidThreadPool : IThreadPool
    {
        public void QueueWorkItem(Action action) => throw new NotImplementedException();

        public IDisposable Schedule(TimeSpan dueTime, Action action) => throw new NotImplementedException();
    }

    class MockThreadPool : IThreadPool
    {
        public class ScheduledAction
        {
            public double DueInMilliseconds;
            public Action Action;
        }

        public Queue<Action> Actions = new Queue<Action>();
        public List<ScheduledAction> ScheduledActions = new List<ScheduledAction>();

        public void QueueWorkItem(Action action) => Actions.Enqueue(action);

        public void RunNextAction()
        {
            Actions.Dequeue()(); // deliberately throw if there's nothing in the queue
        }

        public IDisposable Schedule(TimeSpan dueTime, Action action)
        {
            var dueInMilliseconds = dueTime.TotalMilliseconds;
            int insertionIndex = 0;
            for (insertionIndex = 0; insertionIndex < ScheduledActions.Count; insertionIndex++)
            {
                if (ScheduledActions[insertionIndex].DueInMilliseconds > dueInMilliseconds)
                    break;
            }

            var scheduledAction = new ScheduledAction
            {
                DueInMilliseconds = dueInMilliseconds,
                Action = action
            };
            ScheduledActions.Insert(insertionIndex, scheduledAction);

            return new AnonymousDisposable(() => {
                ScheduledActions.Remove(scheduledAction);
            });
        }

        public void AdvanceClock(TimeSpan elapsedTime)
        {
            var elapsedInMilliseconds = elapsedTime.TotalMilliseconds;
            int cutoffIndex = 0;
            for (int i = 0 ; i < ScheduledActions.Count; i++)
            {
                if (ScheduledActions[i].DueInMilliseconds <= elapsedInMilliseconds)
                {
                    ScheduledActions[i].Action();
                    cutoffIndex = i+1;
                }

                ScheduledActions[i].DueInMilliseconds -= elapsedInMilliseconds;
            }

            ScheduledActions.RemoveRange(0, cutoffIndex);
        }
    }

    static class AssertEx
    {
        public static void Throws<T>(Action action) where T : Exception
        {
            T exception = null;
            try
            {
                action();
            }
            catch (T caught)
            {
                exception = caught;
            }
            Assert.IsNotNull(exception, string.Format("Expected to catch {0} but did not", typeof(T)));
        }
    }
}
