namespace NoPonto.Application.GPS;

/// <summary>Opt-in local contract. Closing is operational finalization, Close is quality finalization.</summary>
public static class EtaTripEvidenceV2
{
    public const string Contract = "eta-trip-evidence-v2";

    public static void Validate(EtaEvidenceEvent e)
    {
        if (e.Contract != Contract) throw new FormatException("Unknown v2 contract");
        // Reuse envelope shape/masks/digest validation without changing the v1 policy.
        var shape = e with { Contract = EtaTripEvidence.Contract, Kind = e.Kind == "Closing" ? "Transition" : e.Kind };
        EtaTripEvidence.Validate(shape with { Digest = EtaTripEvidence.Digest(shape) });
        if (e.Digest != EtaTripEvidence.Digest(e)
            || (e.Kind == "Closing" && string.IsNullOrEmpty(e.OperationalEventId)))
            throw new FormatException("Invalid v2 finalization/digest");
    }

    public static EtaEvidenceHead Apply(EtaEvidenceHead? head, EtaEvidenceEvent e)
    {
        Validate(e);
        if (head is null)
        {
            var begin = e with { Contract = EtaTripEvidence.Contract };
            EtaTripEvidence.Apply(null, begin with { Digest = EtaTripEvidence.Digest(begin) });
            return new(e.GpsUs!.Value, e);
        }
        var p = head.Last;
        if (p.Contract != Contract || p.Kind == "Close" || e.Kind == "Begin"
            || e.Sequence != p.Sequence + 1 || e.PreviousDigest != p.Digest
            || e.TripId != p.TripId || e.ScopeHash != p.ScopeHash || e.EpochId != p.EpochId
            || e.OwnerToken != p.OwnerToken || e.ProfileHash != p.ProfileHash || e.IdentityHash != p.IdentityHash
            || e.CoveredFromUs != p.CoveredThroughUs || e.CoveredThroughUs < p.CoveredThroughUs
            || e.RecordedUs < p.RecordedUs || e.OperationalVersion < p.OperationalVersion
            || e.Admitted < p.Admitted || e.Settled < p.Settled
            || (e.NegativeFlags & p.NegativeFlags) != p.NegativeFlags
            || (e.UnknownFlags & p.UnknownFlags) != p.UnknownFlags)
            throw new FormatException("v2 continuity/identity conflict");
        if (e.Kind == "Closing" && e.OperationalVersion <= p.OperationalVersion)
            throw new FormatException("Closing must follow an operational commit");
        if (p.Kind == "Closing" && e.Kind != "Close")
            throw new FormatException("Closing accepts only resolved quality Close");
        if (e.Kind == "Close" && (p.Kind != "Closing" || e.Pending != 0
            || e.Admitted != p.Admitted || e.OperationalVersion != p.OperationalVersion
            || e.CoveredThroughUs != p.CoveredThroughUs
            || e.OperationalEventId != p.OperationalEventId || e.CoveredThroughUs <= head.BeginGpsUs))
            throw new FormatException("Close requires resolved finalization with the same operational event/version");
        if (e.CoverageProven && (e.Kind == "Checkpoint" || e.CoveredThroughUs > p.CoveredThroughUs)
            && (e.Admitted <= p.Admitted || e.Settled <= p.Settled || e.WitnessHash == p.WitnessHash))
            throw new FormatException("Heartbeat is not coverage");
        if (e.CoverageProven && e.Settled > p.Settled && e.WitnessHash == p.WitnessHash)
            throw new FormatException("Resolution must change the witness");
        return new(head.BeginGpsUs, e);
    }
}
