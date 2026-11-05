using Bloom.Fulfillment.Data;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Domain;

/// <summary>Entity to proto mapping for the Warehouse, Support and Customer APIs.</summary>
public static class Views
{
    public static async Task<V1.PackTask> PackTaskAsync(FulfillmentDbContext db, Guid taskId, CancellationToken ct) =>
        (await PackTasksAsync(db, await db.PackTasks.AsNoTracking().Where(p => p.Id == taskId).ToListAsync(ct), ct)).Single();

    /// <summary>The tasks with their lines (by <c>line_no</c>) and scanned serials (by <c>seq</c>), in the order given; two queries for the whole page.</summary>
    public static async Task<List<V1.PackTask>> PackTasksAsync(FulfillmentDbContext db, IReadOnlyList<Data.PackTask> tasks, CancellationToken ct)
    {
        var ids = tasks.Select(t => t.Id).ToList();
        var lines = (await db.PackTaskLines.AsNoTracking().Where(l => ids.Contains(l.TaskId)).OrderBy(l => l.LineNo).ToListAsync(ct)).ToLookup(l => l.TaskId);
        var serials = (await db.PackTaskSerials.AsNoTracking().Where(s => ids.Contains(s.TaskId)).OrderBy(s => s.Seq).ToListAsync(ct)).ToLookup(s => s.TaskId);
        return tasks.Select(t =>
        {
            var view = new V1.PackTask
            {
                Id = t.Id.ToString(), OrderId = t.OrderId.ToString(), RefId = t.RefId.ToString(), Kind = t.Kind, Status = t.Status,
                ShippingOption = t.ShippingOption ?? "", Version = t.Version, CreatedAt = Timestamp.FromDateTimeOffset(t.CreatedAt),
            };
            view.Lines.AddRange(lines[t.Id].Select(l =>
            {
                var line = new V1.PackTaskLine
                {
                    LineNo = l.LineNo, Kind = l.Kind, SkuId = l.SkuId.ToString(), ProductId = l.ProductId.ToString(), Serialized = l.Serialized, Quantity = l.Quantity,
                };
                if (l.ParentLineNo is { } parent) line.ParentLineNo = parent; // optional fields have no nullable property
                if (l.CoverageMonths is { } months) line.CoverageMonths = months;
                return line;
            }));
            view.Serials.AddRange(serials[t.Id].Select(s => new V1.ScannedSerial { LineNo = s.LineNo, Serial = s.Serial, Seq = s.Seq }));
            return view;
        }).ToList();
    }

    public static V1.Shipment Shipment(Data.Shipment s) => new()
    {
        Id = s.Id.ToString(), OrderId = s.OrderId.ToString(), Direction = s.Direction, Service = s.Service, Status = s.Status,
        TrackingNumber = s.TrackingNumber ?? "", LabelUrl = s.LabelUrl ?? "", Version = s.Version,
    };
}
