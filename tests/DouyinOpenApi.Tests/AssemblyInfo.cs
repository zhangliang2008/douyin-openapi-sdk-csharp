using Xunit;

// SDK 里存在进程级共享状态（HttpClient 实例、测试用的 HttpClientFactory 注入点），
// 测试间并发执行会互相污染（例如「注入必失败 Handler」的用例会把别的用例一起带下水），
// 因此整个测试程序集串行执行。用例数量少，串行的代价可以忽略。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
