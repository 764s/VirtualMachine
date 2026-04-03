using System;
using System.Collections.Generic;
using FFVM;
using FFVM.Compiler;

class Diag
{
    static void Main()
    {
        var compiler = new BytecodeCompiler();
        string source = @"
func helper(n: int): int {
    var local_h: int = n + 100
    Report(local_h)
    return local_h
}

func main() {
    var local_m: int = 5
    var result: int = helper(local_m)
}";
        var syscalls = new Dictionary<string, int> { { "Report", 0 } };
        var result = compiler.Compile(source, "main", syscalls);
        if (!result.Success)
        {
            Console.WriteLine("COMPILE ERROR: " + string.Join(", ", result.Errors));
            return;
        }
        
        Console.WriteLine("Instructions:");
        for (int i = 0; i < result.Program.Instructions.Length; i++)
        {
            var ins = result.Program.Instructions[i];
            int line = result.Program.SourceMap != null ? result.Program.SourceMap[i] : -1;
            Console.WriteLine($"  [{i:D3}] line={line} {ins.Code,-20} A={ins.A} B={ins.B} C={ins.C}");
        }
        
        Console.WriteLine("\nFunctions:");
        for (int i = 0; i < result.Program.Functions.Length; i++)
        {
            var f = result.Program.Functions[i];
            Console.WriteLine($"  {f.Name}: entryIP={f.EntryIP} params={f.ParamCount} locals={f.LocalRegCount}");
        }
        
        Console.WriteLine("\nSymbols:");
        for (int i = 0; i < result.Program.SymbolTable.Length; i++)
        {
            var s = result.Program.SymbolTable[i];
            Console.WriteLine($"  {s.ScopeFunctionName}.{s.Name}: reg={s.Register} fields={s.FieldCount}");
        }
        
        Console.WriteLine("\nConstants:");
        for (int i = 0; i < result.Program.Constants.Length; i++)
            Console.WriteLine($"  [{i}] = {result.Program.Constants[i].ToInt()}");
    }
}
