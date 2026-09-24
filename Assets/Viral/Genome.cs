using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What a virus carries in its head: DNA strands (genes), and the one loaded for injection.
/// Pure data plus a selection event. GenomeView draws the strands floating in the head (focus
/// mode, click the virus); injection (to come: InjectionDrill into the cell) reads
/// <see cref="SelectedGene"/>. Made on demand with default genes when a virus has none.
/// </summary>
public class Genome : MonoBehaviour
{
    [Serializable]
    public class Gene
    {
        [Tooltip("Short code shown in the head view.")]
        public string code = "GEN-0";
        [Tooltip("What it does, shown under the code.")]
        public string name = "UNKNOWN";
        public Color color = TerminalUI.Line;
    }

    public List<Gene> genes = new List<Gene>
    {
        new Gene { code = "LYS-1", name = "LYSIS",      color = new Color(1f, 0.36f, 0.42f) },
        new Gene { code = "REP-2", name = "REPLICASE",  color = new Color(0.55f, 0.93f, 1f) },
        new Gene { code = "CAP-3", name = "CAPSID",     color = new Color(0.62f, 1f, 0.45f) },
        new Gene { code = "SPK-4", name = "SPIKE",      color = new Color(1f, 0.78f, 0.35f) },
        new Gene { code = "INT-5", name = "INTEGRASE",  color = new Color(0.78f, 0.55f, 1f) },
    };

    /// <summary>The virus's head (split off its body so it can grow; the ball inside it is where the
    /// head view grows from). Made on first use.</summary>
    public VirusHead Head => _head ? _head : _head = VirusHead.Of(this);
    VirusHead _head;

    /// <summary>Pointed at (VirusMovement): the head grows.</summary>
    public bool Hovered { set => Head.Hovered = value; }

    /// <summary>The ball inside the head, as a sphere in the world.</summary>
    public Vector3 HeadSphere(out float radius) => Head.Sphere(out radius);

    /// <summary>Index of the gene loaded for injection, -1 none.</summary>
    public int Selected { get; private set; } = -1;
    public Gene SelectedGene => Selected >= 0 && Selected < genes.Count ? genes[Selected] : null;

    /// <summary>(genome, new selection or -1).</summary>
    public event Action<Genome, int> SelectionChanged;

    /// <summary>Load a gene (-1 or out of range unloads).</summary>
    public void Select(int index)
    {
        if (index < 0 || index >= genes.Count) index = -1;
        if (index == Selected) return;
        Selected = index;
        SelectionChanged?.Invoke(this, index);
    }

    /// <summary>(genome, gene) when a gene has been delivered into the cell (for now: the head view's
    /// test animation, down the drill).</summary>
    public event Action<Genome, int> Injected;

    public void Delivered(int index)
    {
        if (index >= 0 && index < genes.Count) Injected?.Invoke(this, index);
    }

    public static Genome Of(Component owner)
    {
        Genome g = owner.GetComponentInChildren<Genome>(true);
        return g ? g : owner.gameObject.AddComponent<Genome>(); // not ??: Unity's fake null
    }
}
