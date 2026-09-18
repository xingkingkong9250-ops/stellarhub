using Xunit;

// SQLite 测试共享全局连接池，ClearAllPools 会 dispose 其它并行测试
// 正在使用的连接（Release 下实测偶发 "Cannot access a disposed object"）。
// 关闭 xUnit 并行，全部测试串行执行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
