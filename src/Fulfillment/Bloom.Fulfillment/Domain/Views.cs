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

    /// <summary>The returns with their units (by <c>unit_no</c>) and label URLs, in the order given; two queries for the whole page.</summary>
    public static async Task<List<V1.ReturnRequest>> ReturnsAsync(FulfillmentDbContext db, IReadOnlyList<Data.ReturnRequest> returns, CancellationToken ct)
    {
        var ids = returns.Select(r => r.Id).ToList();
        var shipmentIds = returns.Select(r => r.ReturnShipmentId).OfType<Guid>().ToList();
        var units = (await db.ReturnUnits.AsNoTracking().Where(u => ids.Contains(u.ReturnId)).OrderBy(u => u.UnitNo).ToListAsync(ct)).ToLookup(u => u.ReturnId);
        var labels = await db.Shipments.AsNoTracking().Where(s => shipmentIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.LabelUrl, ct);
        return returns.Select(r =>
        {
            var view = new V1.ReturnRequest
            {
                Id = r.Id.ToString(), OrderId = r.OrderId.ToString(), Status = r.Status, ExpiresAt = Timestamp.FromDateTimeOffset(r.ExpiresAt),
                LabelUrl = (r.ReturnShipmentId is { } sid ? labels.GetValueOrDefault(sid) : null) ?? "", Version = r.Version, CreatedAt = Timestamp.FromDateTimeOffset(r.CreatedAt),
            };
            view.Units.AddRange(units[r.Id].Select(u => new V1.ReturnUnit
            {
                UnitNo = u.UnitNo, LineNo = u.LineNo, ReceivedSerial = u.ReceivedSerial ?? "", InspectionResult = u.InspectionResult ?? "", InspectionNotes = u.InspectionNotes ?? "",
                Decision = u.Decision ?? "", RefundCents = u.RefundCents, RefundTaxCents = u.RefundTaxCents,
            }));
            return view;
        }).ToList();
    }

    public static async Task<V1.ReturnRequest> ReturnAsync(FulfillmentDbContext db, Guid returnId, CancellationToken ct) =>
        (await ReturnsAsync(db, await db.ReturnRequests.AsNoTracking().Where(r => r.Id == returnId).ToListAsync(ct), ct)).Single();

    /// <summary>The failures with the order of their shipment, in the order given; one extra query for the page.</summary>
    public static async Task<List<V1.DeliveryFailure>> FailuresAsync(FulfillmentDbContext db, IReadOnlyList<Data.DeliveryFailure> failures, CancellationToken ct)
    {
        var ids = failures.Select(f => f.ShipmentId).ToList();
        var orders = await db.Shipments.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.OrderId, ct);
        return failures.Select(f => new V1.DeliveryFailure
        {
            ShipmentId = f.ShipmentId.ToString(), OrderId = orders[f.ShipmentId].ToString(), Kind = f.Kind, Status = f.Status, PreviousStatus = f.PreviousStatus ?? "",
            ReshipId = f.ReshipId?.ToString() ?? "", CreatedAt = Timestamp.FromDateTimeOffset(f.CreatedAt),
        }).ToList();
    }

    public static async Task<V1.DeliveryFailure> FailureAsync(FulfillmentDbContext db, Guid shipmentId, CancellationToken ct) =>
        (await FailuresAsync(db, await db.DeliveryFailures.AsNoTracking().Where(f => f.ShipmentId == shipmentId).ToListAsync(ct), ct)).Single();
}
