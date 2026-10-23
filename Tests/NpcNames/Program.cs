using System.Reflection;
using System.Text;
using CombatSimulator.Npcs;

int checks=0;
void Check(bool condition,string reason){checks++;if(!condition)throw new Exception(reason);}
IReadOnlyDictionary<uint,uint[]> Parse(string csv){using var s=new MemoryStream(Encoding.UTF8.GetBytes(csv));return BattleNpcNameResolver.ReadLinks(s);}
using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(BattleNpcNameResolver.ResourceName)
    ?? throw new Exception("Bundled direct-name resource missing");
var links=BattleNpcNameResolver.ReadLinks(stream);
Check(links.Values.Sum(v=>v.Length)==20280,"Unexpected pinned dataset size");
var names=new Dictionary<uint,string>{{118,"midge swarm"},{53,"unrelated same-number name"},{130,"lindwurm"},{206,"anole stalker"},{462,"snapscales"}};
Check(BattleNpcNameResolver.Resolve(136,links,names).Any(n=>n.NameId==118&&n.Name=="midge swarm"),"Lost directly sourced swarm appearance");
foreach(uint appearance in new uint[]{19257,19271})
    Check(BattleNpcNameResolver.Resolve(appearance,links,names).All(n=>n.NameId!=118),"Discovery graph reintroduced swarm name on a human/demihuman");
var variants=BattleNpcNameResolver.Resolve(53,links,names);
Check(variants.Any(n=>n.NameId==130)&&variants.Any(n=>n.NameId==206)&&variants.Any(n=>n.NameId==462),"Dropped legitimate many-name appearance");
Check(variants.All(n=>n.NameId!=53),"Guessed a name by matching numeric IDs");
var unknown=BattleNpcNameResolver.Resolve(999999,links,names);
Check(unknown.Count==1&&unknown[0]==new BattleNpcName(0,"Battle NPC 999999"),"Unlinked appearance not retained by ID");
Check(BattleNpcNameResolver.Resolve(136,links,new Dictionary<uint,string>())[0].NameId==0,"Missing localized row fabricated a name");
var synthetic=Parse("BNpcNameId,BNpcBaseId\n9,1\n8,1\n9,1\n9,2\n10,1\n");
Check(synthetic[1].SequenceEqual(new uint[]{8,9,10}),"Unstable order or duplicate source rows");
Check(synthetic[2].SequenceEqual(new uint[]{9}),"Collapsed legitimate many-appearance name");
var localized=new Dictionary<uint,string>{{8,"Höhlenwespe"},{9,"Höhlenwespe"},{10," "}};
var resolved=BattleNpcNameResolver.Resolve(1,synthetic,localized);
Check(resolved.Count==1&&resolved[0].Name=="Höhlenwespe"&&resolved[0].NameId==8,"Localized name deduplication or blank filtering failed");
foreach(var invalid in new[]{"", "wrong,header\n1,2", "BNpcNameId,BNpcBaseId\n", "BNpcNameId,BNpcBaseId\n0,1", "BNpcNameId,BNpcBaseId\n1,0", "BNpcNameId,BNpcBaseId\n1,2,3", "BNpcNameId,BNpcBaseId\nx,2"})
{
    bool rejected=false;try{Parse(invalid);}catch(InvalidDataException){rejected=true;}
    Check(rejected,"Malformed/missing data silently accepted");
}
// Every resolved name must be one of the exact input pairs, regardless of graph degree.
var allNames=links.Values.SelectMany(n=>n).Distinct().ToDictionary(n=>n,n=>$"Localized {n}");
foreach(var (appearance,ids) in links)
    foreach(var result in BattleNpcNameResolver.Resolve(appearance,links,allNames))
        Check(ids.Contains(result.NameId),"Resolver invented a pair outside direct source data");
Console.WriteLine($"PASS {checks} checks; {links.Count} base rows / 20,280 direct pairs, swarm regressions, localization, variants, missing-name fallback and strict CSV validation.");
