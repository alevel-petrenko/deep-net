// From Stephen:
// This is the code that was written live on-stage on October 25, 2023.
// I've recreated a few of the tests / examples that I overwrote / deleted
// as I was building up the layers of functionality.

// Recommended follow-up reading:
// https://devblogs.microsoft.com/dotnet/how-async-await-really-works/

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

// 1. Original test of MyThreadPool
{
    for (int i = 0; i < 1000; i++)
    {
        int localI = i;
        MyThreadPool.QueueUserWorkItem(delegate
        {
            Console.WriteLine(localI);
            Thread.Sleep(1000);
        });
    }
    Console.ReadLine();
}

// 2. Revised test to show MyThreadPool having same general behavior as ThreadPool
{
    for (int i = 0; i < 1000; i++)
    {
        int localI = i;
        ThreadPool.QueueUserWorkItem(delegate
        {
            Console.WriteLine(localI);
            Thread.Sleep(1000);
        });
    }
    Console.ReadLine();
}

// 3. Revised test to show ThreadPool flowing AsyncLocals
{
    AsyncLocal<int> localI = new();
    for (int i = 0; i < 1000; i++)
    {
        localI.Value = i;
        ThreadPool.QueueUserWorkItem(delegate
        {
            Console.WriteLine(localI.Value);
            Thread.Sleep(1000);
        });
    }
    Console.ReadLine();
}

// 4. Revised test to show MyThreadPool flowing AsyncLocals
{
    AsyncLocal<int> localI = new();
    for (int i = 0; i < 1000; i++)
    {
        localI.Value = i;
        MyThreadPool.QueueUserWorkItem(delegate
        {
            Console.WriteLine(localI.Value);
            Thread.Sleep(1000);
        });
    }
    Console.ReadLine();
}

// 5. Revised test to show MyTask.Run working
{
    AsyncLocal<int> localI = new();
    List<MyTask> tasks = new();
    for (int i = 0; i < 100; i++)
    {
        localI.Value = i;
        tasks.Add(MyTask.Run(delegate
        {
            Console.WriteLine(localI.Value);
            Thread.Sleep(1000);
        }));
    }
    tasks.ForEach(t => t.Wait());
}

// 6. Showing ContinueWith working
{
    Console.Write("Hello, ");
    MyTask.Delay(TimeSpan.FromSeconds(1)).ContinueWith(() =>
    {
        Console.Write("Warsaw!");
    }).Wait();
}

// 7. Showing ContinueWith working with unwrapping
{
    Console.Write("Hello, ");
    MyTask.Delay(TimeSpan.FromSeconds(1)).ContinueWith(() =>
    {
        Console.Write("Warsaw! ");
        return MyTask.Delay(TimeSpan.FromSeconds(1)).ContinueWith(() =>
        {
            Console.Write("Thank you ");
            return MyTask.Delay(TimeSpan.FromSeconds(1)).ContinueWith(() =>
            {
                Console.Write("for having me! ");
            });
        });
    }).Wait();
}

// 8. Using our Iterate method
{
    MyTask.Iterate(SayHello()).Wait();
    static IEnumerable<MyTask> SayHello()
    {
        while (true)
        {
            Console.Write("Hello, ");
            yield return MyTask.Delay(TimeSpan.FromSeconds(1));
            Console.Write("Warsaw! ");
            yield return MyTask.Delay(TimeSpan.FromSeconds(1));
            Console.Write("Thank you ");
            yield return MyTask.Delay(TimeSpan.FromSeconds(1));
            Console.Write("for having me! ");
        }
    }
}

// 9. Using MyTask with the real async/await
{
    SayHello().Wait();
    static async MyTask SayHello()
    {
        while (true)
        {
            Console.Write("Hello, ");
            await MyTask.Delay(TimeSpan.FromSeconds(1));
            Console.Write("Warsaw! ");
            await MyTask.Delay(TimeSpan.FromSeconds(1));
            Console.Write("Thank you ");
            await MyTask.Delay(TimeSpan.FromSeconds(1));
            Console.Write("for having me! ");
        }
    }
}

class MyTaskAsyncMethodBuilder
{
    public static MyTaskAsyncMethodBuilder Create() => new();
    public MyTask Task { get; } = new();

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        ExecutionContext? ec = ExecutionContext.Capture();
        try
        {
            stateMachine.MoveNext();
        }
        finally
        {
            if (ec is not null)
            {
                ExecutionContext.Restore(ec);
            }
        }
    }

    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    public void SetResult() => Task.SetResult();
    public void SetException(Exception e) => Task.SetException(e);

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter,
        ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine =>
        awaiter.OnCompleted(stateMachine.MoveNext);
    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter,
        ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine =>
        awaiter.OnCompleted(stateMachine.MoveNext);
}

[AsyncMethodBuilder(typeof(MyTaskAsyncMethodBuilder))]
class MyTask
{
    private bool _completed;
    private Exception? _exception;
    private Action? _continuation;
    private ExecutionContext? _ec;

    public struct Awaiter(MyTask task) : INotifyCompletion
    {
        public bool IsCompleted => task.IsCompleted;
        public void GetResult() => task.Wait();
        public void OnCompleted(Action action) => task.ContinueWith(action);
    }

    public Awaiter GetAwaiter() => new(this);

    public bool IsCompleted
    {
        get
        {
            lock (this)
            {
                return _completed;
            }
        }
    }

    public void Wait()
    {
        ManualResetEventSlim? mres = null;
        lock (this)
        {
            if (!_completed)
            {
                mres = new ManualResetEventSlim();
                ContinueWith(mres.Set);
            }
        }

        mres?.Wait();
        if (_exception is not null)
        {
            ExceptionDispatchInfo.Throw(_exception);
        }
    }

    public MyTask ContinueWith(Action action)
    {
        var task = new MyTask();
        Action continuation = () =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                task.SetException(ex);
                return;
            }
            task.SetResult();
        };

        lock (this)
        {
            if (_completed)
            {
                MyThreadPool.QueueUserWorkItem(continuation);
            }
            else
            {
                if (_continuation is not null)
                {
                    throw new InvalidOperationException("This is not the Task you're looking for");
                }

                _continuation = continuation;
                _ec = ExecutionContext.Capture();
            }
        }
        return task;
    }

    public MyTask ContinueWith(Func<MyTask> action)
    {
        var task = new MyTask();
        Action continuation = () =>
        {
            try
            {
                MyTask newTask = action();
                newTask.ContinueWith(() =>
                {
                    if (newTask._exception is not null)
                    {
                        task.SetException(newTask._exception);
                    }
                    else
                    {
                        task.SetResult();
                    }
                });
            }
            catch (Exception ex)
            {
                task.SetException(ex);
                return;
            }
        };

        lock (this)
        {
            if (_completed)
            {
                MyThreadPool.QueueUserWorkItem(continuation);
            }
            else
            {
                if (_continuation is not null)
                {
                    throw new InvalidOperationException("This is not the Task you're looking for");
                }

                _continuation = continuation;
                _ec = ExecutionContext.Capture();
            }
        }
        return task;
    }

    public void SetResult() => Complete(null);

    public void SetException(Exception exception) => Complete(exception);

    private void Complete(Exception? exception)
    {
        lock (this)
        {
            if (_completed)
            {
                throw new InvalidOperationException("Stop messing up my demo");
            }

            _completed = true;
            _exception = exception;

            if (_continuation is not null)
            {
                MyThreadPool.QueueUserWorkItem(() =>
                {
                    if (_ec is null)
                    {
                        _continuation();
                    }
                    else
                    {
                        ExecutionContext.Run(_ec, _ => _continuation(), null);
                    }
                });
            }
        }
    }

    public static MyTask Run(Action action)
    {
        var task = new MyTask();
        MyThreadPool.QueueUserWorkItem(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                task.SetException(e);
                return;
            }
            task.SetResult();
        });
        return task;
    }

    public static MyTask Delay(TimeSpan delay)
    {
        var task = new MyTask();
        new Timer(_ => task.SetResult()).Change(delay, Timeout.InfiniteTimeSpan);
        return task;
    }

    public static MyTask WhenAll(MyTask task1, MyTask task2)
    {
        var task = new MyTask();

        int remaining = 2;
        Action continuation = () =>
        {
            if (Interlocked.Decrement(ref remaining) == 0)
            {
                // Ignoring propagating exceptions for demo purposes
                task.SetResult();
            }
        };

        task1.ContinueWith(continuation);
        task2.ContinueWith(continuation);

        return task;
    }

    public static MyTask Iterate(IEnumerable<MyTask> tasks)
    {
        var task = new MyTask();

        IEnumerator<MyTask> e = tasks.GetEnumerator();

        void MoveNext()
        {
            try
            {
                if (e.MoveNext())
                {
                    MyTask nextTask = e.Current;
                    nextTask.ContinueWith(MoveNext);
                    return;
                }
            }
            catch (Exception ex)
            {
                task.SetException(ex);
                return;
            }
            task.SetResult();
        }
        MoveNext();

        return task;
    }
}

static class MyThreadPool
{
    private static readonly BlockingCollection<(Action, ExecutionContext?)> s_workItems = new();

    public static void QueueUserWorkItem(Action action) => s_workItems.Add((action, ExecutionContext.Capture()));

    static MyThreadPool()
    {
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            new Thread(() =>
            {
                while (true)
                {
                    (Action action, ExecutionContext? ec) = s_workItems.Take();
                    if (ec is null)
                    {
                        action();
                    }
                    else
                    {
                        ExecutionContext.Run(ec, _ => action(), null);
                    }
                }
            })
            { IsBackground = true }.Start();
        }
    }
}
