using System.Globalization;
using System.Reflection;

namespace OneDiff.Tests.Wasm;

/// <summary>
/// A small test runner for the browser: finds [Fact] and [Theory] methods in this assembly by reflection, runs them and prints
/// one line per failure and a final RESULT line. Theory rows come from the xunit data attributes themselves, so the sources run
/// unchanged. Returns 0 only when at least one test ran and none failed.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string filter = args.Length > 0 ? args[0] : string.Empty;
        int total = 0, passed = 0, failed = 0, skipped = 0;

        foreach (Type type in typeof(Program).Assembly.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (!type.IsClass || type.IsAbstract || !type.IsPublic)
            {
                continue;
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                FactAttribute? fact = method.GetCustomAttribute<FactAttribute>();
                if (fact is null)
                {
                    continue;
                }

                string name = type.FullName + "." + method.Name;
                if (!name.Contains(filter, StringComparison.Ordinal))
                {
                    continue;
                }

                if (fact.Skip is not null)
                {
                    skipped++;
                    total++;
                    continue;
                }

                List<object?[]> rows;
                try
                {
                    rows = await RowsAsync(method, fact);
                }
                catch (Exception e)
                {
                    total++;
                    failed++;
                    Console.WriteLine("FAIL " + name + " (reading theory data): " + e.Message);
                    continue;
                }

                foreach (object?[] row in rows)
                {
                    total++;
                    string display = row.Length == 0 ? name : name + "(" + string.Join(", ", row.Select(Show)) + ")";
                    string? error = await RunAsync(type, method, row);
                    if (error is null)
                    {
                        passed++;
                    }
                    else
                    {
                        failed++;
                        Console.WriteLine("FAIL " + display + ": " + error);
                    }
                }
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"RESULT total={total} passed={passed} failed={failed} skipped={skipped}"));
        return failed == 0 && total > 0 ? 0 : 1;
    }

    private static async Task<List<object?[]>> RowsAsync(MethodInfo method, FactAttribute fact)
    {
        var rows = new List<object?[]>();
        if (fact is not TheoryAttribute)
        {
            rows.Add([]);
            return rows;
        }

        var tracker = new Xunit.Sdk.DisposalTracker();
        foreach (Xunit.v3.IDataAttribute data in method.GetCustomAttributes().OfType<Xunit.v3.IDataAttribute>())
        {
            // The attribute's own GetData yields nothing for member data outside a real test run, so members are read directly.
            if (data is Xunit.v3.MemberDataAttributeBase member)
            {
                rows.AddRange(MemberRows(method, member));
                continue;
            }

            foreach (ITheoryDataRow row in await data.GetData(method, tracker))
            {
                rows.Add(row.GetData());
            }
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException("theory has no data");
        }

        return rows;
    }

    private static IEnumerable<object?[]> MemberRows(MethodInfo method, Xunit.v3.MemberDataAttributeBase member)
    {
        Type owner = member.MemberType ?? method.ReflectedType ?? throw new InvalidOperationException("no declaring type");
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        object? value;
        PropertyInfo? property = owner.GetProperty(member.MemberName, Flags);
        FieldInfo? field = owner.GetField(member.MemberName, Flags);
        MethodInfo? getter = owner.GetMethod(member.MemberName, Flags);
        if (property is not null)
        {
            value = property.GetValue(null);
        }
        else if (field is not null)
        {
            value = field.GetValue(null);
        }
        else if (getter is not null)
        {
            value = getter.Invoke(null, member.Arguments);
        }
        else
        {
            throw new InvalidOperationException("member data " + member.MemberName + " not found on " + owner.Name);
        }

        foreach (object? item in (System.Collections.IEnumerable)value!)
        {
            yield return item switch
            {
                ITheoryDataRow row => row.GetData(),
                object?[] array => array,
                _ => throw new InvalidOperationException("member data rows must be object arrays or theory data rows"),
            };
        }
    }

    private static async Task<string?> RunAsync(Type type, MethodInfo method, object?[] row)
    {
        object? instance = null;
        try
        {
            if (!method.IsStatic)
            {
                instance = Activator.CreateInstance(type);
            }

            object? result = method.Invoke(instance, row);
            switch (result)
            {
                case Task task:
                    await task;
                    break;
                case ValueTask valueTask:
                    await valueTask;
                    break;
            }

            return null;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            return Describe(e.InnerException);
        }
        catch (Exception e)
        {
            return Describe(e);
        }
        finally
        {
            if (instance is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (instance is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static string Describe(Exception e)
    {
        string text = e.GetType().Name + ": " + e.Message;
        return text.Length > 600 ? string.Concat(text.AsSpan(0, 600), "…") : text;
    }

    private static string Show(object? value) => value switch
    {
        null => "null",
        string s => "\"" + (s.Length > 40 ? string.Concat(s.AsSpan(0, 40), "…") : s) + "\"",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
