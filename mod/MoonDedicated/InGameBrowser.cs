using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Il2CppInterop.Runtime.Injection;

namespace MoonDedicated
{
    // Uses Unity's own GUI inside the game process. Runtime binding keeps the
    // plugin build independent of copyrighted/generated game assemblies.
    public static unsafe class InGameBrowser
    {
        sealed class Entry { public string name { get; set; } = ""; public string address { get; set; } = ""; public bool favorite { get; set; } }
        sealed class Store { public string game_dir { get; set; } = ""; public List<Entry> servers { get; set; } = new List<Entry>(); }
        [StructLayout(LayoutKind.Sequential)] struct Rect { public float x,y,w,h; public Rect(float x,float y,float w,float h) { this.x=x;this.y=y;this.w=w;this.h=h; } }
        static Store store = new Store();
        static string loadError = "", status = "", label = "", address = "", password = "", queryAddress = "";
        static bool editing;
        static int selected = -1, page;
        static int editIndex = -1;
        static bool connecting;
        static Task<ServerQuery> query;
        static readonly Dictionary<string, ServerQuery> results = new Dictionary<string, ServerQuery>();
        static readonly Dictionary<string, long> queriedAt = new Dictionary<string, long>();
        static readonly List<object> roots = new List<object>();
        static IntPtr gui, labelMethod, screenW, screenH;
        static bool installed;
        static IntPtr eventClass, drawTexture, setColor, whiteTexture, labelStyle;
        static int eventType, keyCode, pressedControl, focusedControl, caret;
        static bool selectAll, controlKey;
        static char typedCharacter;
        static float mouseX, mouseY;
        [StructLayout(LayoutKind.Sequential)] struct Color { public float r,g,b,a; public Color(float r,float g,float b,float a=1) {this.r=r;this.g=g;this.b=b;this.a=a;} }

        public static bool IsOpen { get; private set; }
        public static int DrawCount { get; private set; }
        public static string LastError { get; private set; } = "";
        static float scale, left, top;
        static string StorePath => Path.Combine(Environment.GetEnvironmentVariable("MOON_DS_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MoonDS"), "servers.json");

        public static void Install(object plugin)
        {
            var baseType = Assembly.Load("UnityEngine.CoreModule").GetType("UnityEngine.MonoBehaviour", true);
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MoonDedicated.UI"), AssemblyBuilderAccess.Run);
            var type = assembly.DefineDynamicModule("UI").DefineType("MoonDedicatedBrowserGui", TypeAttributes.Public, baseType);
            var ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(IntPtr) });
            var il = ctor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, baseType.GetConstructor(new[] { typeof(IntPtr) })); il.Emit(OpCodes.Ret);
            var draw = type.DefineMethod("OnGUI", MethodAttributes.Public, typeof(void), Type.EmptyTypes);
            il = draw.GetILGenerator(); il.Emit(OpCodes.Call, typeof(InGameBrowser).GetMethod(nameof(Render))); il.Emit(OpCodes.Ret);
            Type component = type.CreateType();
            ClassInjector.RegisterTypeInIl2Cpp(component);
            roots.Add(plugin.GetType().GetMethod("AddComponent").MakeGenericMethod(component).Invoke(plugin, null));
            gui = Il2.Class(Il2.Image("UnityEngine.IMGUIModule.dll"), "UnityEngine", "GUI");
            labelMethod = Il2.FindMethod(gui, "Label", m => Il2.ParamCount(m)==3 && Il2.ParamType(m,1)=="System.String");
            drawTexture = Il2.Method(gui,"DrawTexture",2); setColor=Il2.Method(gui,"set_color",1);
            whiteTexture=Il2.Invoke(Il2.Method(Il2.Class(Game.CoreImage,"UnityEngine","Texture2D"),"get_whiteTexture",0),IntPtr.Zero);
            eventClass=Il2.Class(Il2.Image("UnityEngine.IMGUIModule.dll"),"UnityEngine","Event");
            var screen = Il2.Class(Game.CoreImage, "UnityEngine", "Screen");
            screenW = Il2.Method(screen, "get_width", 0); screenH = Il2.Method(screen, "get_height", 0);
            if (new[] { labelMethod,drawTexture,setColor,whiteTexture,eventClass,screenW,screenH }.Any(p=>p==IntPtr.Zero)) throw new MissingMethodException("Unity browser rendering API unavailable");
            installed = true;
        }

        public static void Open()
        {
            if (!installed) throw new InvalidOperationException("In-game browser is not initialized");
            if (!Game.InMenu() || Game.Loading()) return;
            Load(); password = ""; status = "Select a saved server or add a host address. Query port defaults to 27026.";
            selected = store.servers.Count > 0 ? 0 : -1; page = 0; editing = false;
            IsOpen = true; connecting = false; MenuButton.ShowHome(false);
            if (selected >= 0) Refresh();
        }
        public static void Close() { IsOpen = false; connecting = false; password = ""; editing = false; MenuButton.ShowHome(true); }
        static void Load()
        {
            loadError = "";
            try {
                store = File.Exists(StorePath) ? JsonSerializer.Deserialize<Store>(File.ReadAllText(StorePath)) ?? throw new IOException("Empty saved-server document") : new Store();
                if (store.servers == null || store.servers.Any(e => e == null || e.address == null || e.name == null)) throw new IOException("Invalid saved-server entries");
            } catch (Exception e) { store = new Store(); loadError = "Saved servers could not be loaded. Original file retained: " + e.Message; }
        }
        static bool Save()
        {
            if (loadError.Length > 0) { status = loadError; return false; }
            string tmp = "";
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
                using var lease = new FileStream(Path.Combine(Path.GetDirectoryName(StorePath), "servers.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                tmp = StorePath + "." + Environment.ProcessId + ".tmp";
                using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write)) {
                    JsonSerializer.Serialize(f, store); f.Flush(true);
                }
                File.Move(tmp, StorePath, true); return true;
            } catch (Exception e) { status = "Could not save servers: " + e.Message; return false; }
            finally { if (tmp.Length > 0) { try { File.Delete(tmp); } catch { } } }
        }
        static void Refresh()
        {
            if (selected < 0 || selected >= store.servers.Count) return;
            if (query != null) { status = "A query is already running; please wait."; return; }
            queryAddress = store.servers[selected].address;
            results.Remove(queryAddress); queriedAt.Remove(queryAddress);
            query = ServerQuery.Fetch(queryAddress); status = "Querying " + queryAddress + "...";
        }
        static void Pump()
        {
            if (connecting && PlayerClient.LastNotice.Length > 0) status = PlayerClient.LastNotice;
            if (query == null || !query.IsCompleted) return;
            try { results[queryAddress] = query.GetAwaiter().GetResult(); queriedAt[queryAddress] = Environment.TickCount64; status = "Server details updated."; }
            catch (Exception e) { status = "Query failed: " + e.Message; }
            query = null;
        }
        static int Id(float x,float y) => (int)x*1000+(int)y+1;
        static bool Over(Rect rect) => mouseX>=rect.x && mouseX<=rect.x+rect.w && mouseY>=rect.y && mouseY<=rect.y+rect.h;
        static void Fill(Rect rect,Color color)
        {
            Il2.Invoke(setColor,IntPtr.Zero,(IntPtr)(&color));
            Il2.Invoke(drawTexture,IntPtr.Zero,(IntPtr)(&rect),whiteTexture);
            color=new Color(1,1,1);Il2.Invoke(setColor,IntPtr.Zero,(IntPtr)(&color));
        }
        static bool Button(string text, float x, float y, float w, float h = 38)
        {
            var rect=R(x,y,w,h); int id=Id(x,y); bool over=Over(rect);
            Fill(rect,over?new Color(.29f,.46f,.64f):new Color(.16f,.23f,.32f));
            Label(text,x+10,y+5,w-20,h-5);
            if(eventType==0 && over) { pressedControl=id; focusedControl=0; Use(); }
            if(eventType==1 && pressedControl==id) { pressedControl=0;if(over){Use();return true;} }
            return false;
        }
        static Rect R(float x,float y,float w,float h) => new Rect(left+x*scale,top+y*scale,w*scale,h*scale);
        static void Label(string text,float x,float y,float w,float h = 32)
        { var rect=R(x,y,w,h); Il2.Invoke(labelMethod,IntPtr.Zero,(IntPtr)(&rect),Il2.NewString(text),labelStyle); }
        static void Use() { var ev=Il2.Invoke(Il2.Method(eventClass,"get_current",0),IntPtr.Zero);Il2.Invoke(Il2.Method(eventClass,"Use",0),ev);eventType=-1; }
        static string Field(string value,float x,float y,float w,bool secret = false)
        {
            var rect=R(x,y,w,38); int id=Id(x,y), max=secret?128:260;
            bool focus=focusedControl==id;
            if(eventType==0 && Over(rect)) { focusedControl=id;caret=value.Length;selectAll=false;focus=true;Use(); }
            if(focus && eventType==4) {
                caret=Math.Clamp(caret,0,value.Length);
                string insert="";
                if(controlKey && (keyCode==97 || keyCode==65)) { selectAll=true;Use(); }
                else if(controlKey && (keyCode==118 || keyCode==86)) {
                    var utility=Il2.Class(Il2.Image("UnityEngine.IMGUIModule.dll"),"UnityEngine","GUIUtility");
                    insert=Il2.ReadString(Il2.Invoke(Il2.Method(utility,"get_systemCopyBuffer",0),IntPtr.Zero)); Use();
                } else if(keyCode==8 || keyCode==127) {
                    if(selectAll) {value="";caret=0;selectAll=false;}
                    else if(keyCode==8 && caret>0) {int n=caret>1 && char.IsLowSurrogate(value[caret-1])?2:1;value=value.Remove(caret-n,n);caret-=n;}
                    else if(keyCode==127 && caret<value.Length) {int n=char.IsHighSurrogate(value[caret]) && caret+1<value.Length?2:1;value=value.Remove(caret,n);}
                    Use();
                } else if(keyCode==276) {caret=Math.Max(0,caret-1);selectAll=false;Use();}
                else if(keyCode==275) {caret=Math.Min(value.Length,caret+1);selectAll=false;Use();}
                else if(keyCode==278) {caret=0;selectAll=false;Use();}
                else if(keyCode==279) {caret=value.Length;selectAll=false;Use();}
                else if(!controlKey && !char.IsControl(typedCharacter) && typedCharacter!='\0') {insert=typedCharacter.ToString();Use();}
                if(insert.Length>0) {
                    if(selectAll) {value="";caret=0;selectAll=false;}
                    insert=new string(insert.Where(c=>!char.IsControl(c)).Take(max-value.Length).ToArray());
                    value=value.Insert(caret,insert);caret+=insert.Length;
                }
            }
            Fill(rect,focus?new Color(.19f,.28f,.40f):new Color(.09f,.12f,.18f));
            string shown=secret?new string('*',value.Length):value;
            if(focus && (Environment.TickCount64/500)%2==0) shown=shown.Insert(Math.Clamp(caret,0,shown.Length),"|");
            Label(shown,x+8,y+4,w-16,34);
            return value;
        }
        static void Font()
        {
            var skin=Il2.Invoke(Il2.Method(gui,"get_skin",0),IntPtr.Zero);
            labelStyle=Il2.Invoke(Il2.Method(Il2.ClassOf(skin),"get_label",0),skin);
            int size=(int)(19*scale); byte wrap=1;
            Il2.Invoke(Il2.Method(Il2.ClassOf(labelStyle),"set_fontSize",1),labelStyle,(IntPtr)(&size));
            Il2.Invoke(Il2.Method(Il2.ClassOf(labelStyle),"set_wordWrap",1),labelStyle,(IntPtr)(&wrap));
            var ev=Il2.Invoke(Il2.Method(eventClass,"get_current",0),IntPtr.Zero);
            eventType=Il2.Int(Il2.Invoke(Il2.Method(eventClass,"get_type",0),ev));
            var pos=Il2.Invoke(Il2.Method(eventClass,"get_mousePosition",0),ev);
            var p=Il2.Value<Point>(pos);mouseX=p.x;mouseY=p.y;
            keyCode=Il2.Int(Il2.Invoke(Il2.Method(eventClass,"get_keyCode",0),ev));
            typedCharacter=Il2.Value<char>(Il2.Invoke(Il2.Method(eventClass,"get_character",0),ev));
            controlKey=Il2.Bool(Il2.Invoke(Il2.Method(eventClass,"get_control",0),ev)) || Il2.Bool(Il2.Invoke(Il2.Method(eventClass,"get_command",0),ev));
        }
        [StructLayout(LayoutKind.Sequential)] struct Point { public float x,y; }
        static void Join(ServerQuery q)
        {
            try {
                if (q.SteamId == 0) throw new IOException("Server did not advertise its Steam ID");
                if (q.Version != Cfg.GameVersion) throw new IOException("Server game version is " + q.Version + "; client requires " + Cfg.GameVersion);
                if (q.Players >= q.Capacity) throw new IOException("Server is full");
                var problems = Requirements(q);
                if (problems.Count > 0) throw new IOException(string.Join("; ", problems));
                if (q.Password && password.Length == 0) throw new IOException("Enter this server's password first");
                var data = new { address = store.servers[selected].address, steam_id = q.SteamId.ToString(), password,
                    name = q.Name, created_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                PlayerClient.ClearNotice();
                string tmp=Path.Combine(Cfg.ModDir,"connect.json.tmp");
                File.WriteAllText(tmp,JsonSerializer.Serialize(data)); File.Move(tmp,Path.Combine(Cfg.ModDir,"connect.json"),true);
                password=""; status="Connecting to " + q.Name + ". " + "The game will load when the server accepts your connection.";
                connecting=true;
            } catch(Exception e) { status=e.Message; }
        }
        static List<string> Requirements(ServerQuery q)
        {
            var problems=new List<string>();
            if (q.Rules.TryGetValue("moon_policy",out var policy) && policy=="off") return problems;
            var have=Mods.Installed();
            foreach(var spec in q.Rules.GetValueOrDefault("moon_mods", "").Split(',',StringSplitOptions.RemoveEmptyEntries)) {
                var req=ModReq.Parse(spec); var installed=have.FirstOrDefault(m=>m.Guid.Equals(req.Guid,StringComparison.OrdinalIgnoreCase));
                if(installed==null) problems.Add("Missing " + spec);
                else if(req.Version!=null && req.Version!=installed.Version) problems.Add("Needs " + spec + "; installed " + installed.Version);
            }
            return problems;
        }

        public static void Render()
        {
            if (!IsOpen) { Pump(); return; }
            try {
                if (!Game.InMenu() || Game.InGame() || Game.Loading()) { Close(); return; }
                DrawCount++; Pump();
                int width=Il2.Int(Il2.Invoke(screenW,IntPtr.Zero)), height=Il2.Int(Il2.Invoke(screenH,IntPtr.Zero));
                scale=Math.Min(width/1120f,height/740f); left=(width-1100*scale)/2; top=(height-720*scale)/2;
                Font(); var bounds=R(0,0,1100,720); Fill(bounds,new Color(.055f,.075f,.11f,.99f));
                Label("MOON SWEEPERS — DEDICATED SERVERS",24,16,850,36);
                if(Button("Close",950,16,125)) { Close(); return; }
                if(loadError.Length>0) Label(loadError,24,80,1045,150);
                else if(editing) DrawEdit();
                else DrawList();
                Label(status,24,622,1045,70);
                LastError="";
            } catch(Exception e) { LastError=e.Message; status="Browser error: "+e.Message; Log.Error(status); Close(); }
        }
        static void DrawEdit()
        {
            Label("Server label",30,90,300); label=Field(label,30,125,1030);
            Label("Address (host:query-port, default 27026)",30,180,1000); address=Field(address,30,215,1030);
            if(Button("Save server",30,285,200)) {
                try {
                    ServerQuery.Address(address); string a=address.Trim();
                    if(editIndex<0 && store.servers.Count>=1024) throw new IOException("Saved-server limit is 1024");
                    if(store.servers.Where((e,i)=>i!=editIndex).Any(e=>e.address==a)) throw new IOException("This address is already saved");
                    Entry previous=editIndex>=0?store.servers[editIndex]:null;
                    var replacement=new Entry { name=string.IsNullOrWhiteSpace(label)?a:label.Trim(),address=a,favorite=previous?.favorite??false };
                    if(editIndex>=0) store.servers[editIndex]=replacement; else store.servers.Add(replacement);
                    if(Save()) { selected=editIndex>=0?editIndex:store.servers.Count-1; page=selected/6; editing=false; Refresh(); }
                    else if(editIndex>=0) store.servers[editIndex]=previous; else store.servers.RemoveAt(store.servers.Count-1);
                } catch(Exception e) { status=e.Message; }
            }
            if(Button("Cancel",250,285,150)) editing=false;
        }
        static void DrawList()
        {
            if(Button("Add server",24,70,170)) { editing=true;editIndex=-1;label="";address="";status="Enter the address supplied by the server host.";return; }
            if(Button("Refresh selected",210,70,230)) Refresh();
            if(selected>=0 && selected<store.servers.Count) {
                var entry=store.servers[selected];
                if(Button("Edit selected",865,70,210)) { editing=true;editIndex=selected;label=entry.name;address=entry.address;status="Edit the saved label or host address.";return; }
                if(Button(entry.favorite?"Unfavourite":"Favourite",455,70,170)) { entry.favorite=!entry.favorite;Save(); }
                if(Button("Delete selected",640,70,210)) {
                    store.servers.RemoveAt(selected);Save(); selected=-1; page=Math.Min(page,Math.Max(0,(store.servers.Count-1)/6)); password=""; return;
                }
            }
            if(store.servers.Count==0) Label("No saved servers. Add an address to get started.",30,140,1000);
            for(int i=page*6;i<Math.Min(store.servers.Count,page*6+6);i++) {
                var entry=store.servers[i]; string prefix=i==selected?"> ":"";
                if(Button(prefix+(entry.favorite?"* ":"")+entry.name+"   "+entry.address,24,125+(i%6)*47,1048,42)) {
                    selected=i; password=""; Refresh();
                }
            }
            if(page>0 && Button("Previous",24,415,150)) page--;
            Label("Page "+(page+1)+" / "+Math.Max(1,(store.servers.Count+5)/6),195,420,200);
            if((page+1)*6<store.servers.Count && Button("Next",400,415,150)) page++;
            if(selected>=0 && selected<store.servers.Count) {
                var entry=store.servers[selected];
                if(results.TryGetValue(entry.address,out var q)) {
                    string mods=q.Rules.GetValueOrDefault("moon_mods", "");
                    Label(q.Name+"   "+q.Players+"/"+q.Capacity+" players   game "+q.Version+"   "+(q.Password?"Password required":"No password"),24,465,1048);
                    Label("Mods: "+(mods.Length==0?"none required":mods),24,500,1048,50);
                    Label("Password",24,560,140); password=Field(password,165,555,550,true);
                    if(Button("Join server",745,555,200)) {
                        if(Environment.TickCount64-queriedAt.GetValueOrDefault(entry.address)>30000) { status="Details expired. Refresh selected before joining."; }
                        else Join(q);
                    }
                    if(Button("Cancel join",955,555,120)) { PlayerClient.CancelJoin();connecting=false;status="Connection cancelled."; }
                }
            }
        }
    }
}
