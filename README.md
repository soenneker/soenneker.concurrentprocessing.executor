[![](https://img.shields.io/nuget/v/soenneker.concurrentprocessing.executor.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.concurrentprocessing.executor/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.concurrentprocessing.executor/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.concurrentprocessing.executor/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.concurrentprocessing.executor.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.concurrentprocessing.executor/)

# ![](https://user-images.githubusercontent.com/4441470/224455560-91ed3ee7-f510-4041-a8d2-3fc093025112.png) Soenneker.ConcurrentProcessing.Executor

This executor efficiently handles multiple tasks with controlled concurrency. It is ideal for managing parallel execution of tasks while ensuring that no more than a specified number of tasks run simultaneously.

### **Key Features**
- **Concurrent Execution:** Limits the number of concurrent tasks to prevent overloading.
- **Failure Handling with Retry Logic:** Automatically retries failed tasks with exponential backoff.
- **Async Semaphore:** Uses a non-blocking semaphore to control concurrency and ensure thread safety.
- **CancellationToken support** for task cancellation.

⚠️ **Note:**
- This is not a background processor. It **only** manages concurrency for tasks that are provided during execution.

---

### **Installation**
```
dotnet add package Soenneker.ConcurrentProcessing.Executor
```

---

### **Example: Executing Multiple Tasks with Concurrency Control**
```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.ConcurrentProcessing.Executor;

public class Program
{
    public static async Task Main(string[] args)
    {
        var executor = new ConcurrentProcessingExecutor(maxConcurrency: 3);

        var tasks = new List<Func<CancellationToken, ValueTask>>
        {
            async (ct) => { 
                Console.WriteLine("Task 1 started"); 
                await Task.Delay(500, ct); 
                Console.WriteLine("Task 1 completed"); 
            },

            async (ct) => { 
                Console.WriteLine("Task 2 started"); 
                await Task.Delay(300, ct); 
                Console.WriteLine("Task 2 completed"); 
            },

            async (ct) => { 
                Console.WriteLine("Task 3 started"); 
                await Task.Delay(700, ct); 
                Console.WriteLine("Task 3 completed");
            },

            async (ct) => { 
                Console.WriteLine("Task 4 started"); 
                await Task.Delay(400, ct); 
                Console.WriteLine("Task 4 completed"); 
            }
        };

        await executor.Execute(tasks);
    }
}
```

### **Console Output**
```shell
Task 1 started
Task 2 started
Task 3 started
Task 1 completed
Task 4 started
Task 2 completed
Task 3 completed
Task 4 completed
```