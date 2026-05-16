namespace InfiniContext.Core.Tasks;

/// <summary>
/// Filter applied by <see cref="TaskService.ListAsync"/>. Both fields are optional;
/// <c>null</c> means no constraint on that axis.
/// </summary>
/// <param name="Status">If set, return only tasks in this lifecycle state.</param>
/// <param name="Limit">If set, return at most this many tasks (after status filtering). Order is
/// most-recently-active first, matching <see cref="TaskStore.ListAsync"/>.</param>
public sealed record ListFilter(TaskItemStatus? Status = null, int? Limit = null);
