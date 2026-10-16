// Decompiles functions at the given addresses without full auto-analysis.
// Usage (postScript): DecompAt.java <outFile> <addr> [addr...]
import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import java.io.FileWriter;

public class DecompAt extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        try (FileWriter out = new FileWriter(args[0], true)) {
            for (int i = 1; i < args.length; i++) {
                Address a = toAddr(args[i]);
                Function f = getFunctionAt(a);
                if (f == null) {
                    new DisassembleCommand(a, null, true).applyTo(currentProgram, monitor);
                    f = createFunction(a, null);
                }
                if (f == null) { out.write("// could not create function at " + a + "\n"); continue; }
                DecompileResults r = di.decompileFunction(f, 120, monitor);
                out.write("// ===== " + f.getName() + " @ " + a + "\n");
                out.write(r.decompileCompleted() ? r.getDecompiledFunction().getC() : "// decompile failed: " + r.getErrorMessage());
                out.write("\n");
            }
        }
    }
}
