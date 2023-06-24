module Dependency.Monitor

open System.IO
open FSharp.Data
open System.Net.Http
open System.Text.Json
open System.Net.Http.Headers
open System.Threading
open Dependency.Mail
open Dependency.Core

open System
open System.Collections.Generic
open System.Collections.Concurrent

type User = User of email:string
    with member self.ToString = 
        match self with 
        | User email -> email

[<Class>]
type Monitor(recepient: User, config: Config) = 
    let mutable State : ConcurrentDictionary<int, string> = ConcurrentDictionary<_, _>()
    let mutable Flagged : int Set = Set.empty
    let mutable Config : Config = config
    let mutable Email : User = recepient

    let CancellationToken = new CancellationTokenSource()
    let TemporaryFilePath = sprintf "%s.json" (Email.ToString)

    new(path: string, filename:string, config:Config) as self= 
        Monitor(User(filename), config)
        then self.ReadInFile path
             self.Start 10 (Path.GetDirectoryName(path))


    member public _.Current() = (State, Flagged)
    member public _.Path() = TemporaryFilePath

    member private _.GetRequestWithAuth (key:string) eip =
        async {
            let client = new HttpClient()
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json")
            client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("token", key)
            client.DefaultRequestHeaders.UserAgent.Add(ProductInfoHeaderValue("FSharp", "6.0"))
            let! response =  client.GetAsync(sprintf "https://api.github.com/repos/ethereum/EIPs/contents/EIPS/eip-%i.md" eip) |> Async.AwaitTask
            return! response.Content.ReadAsStringAsync() |> Async.AwaitTask
        } |> Async.RunSynchronously
          |> JsonValue.Parse

    member public _.SaveInFile pathPrefix =
        try
            printf "Save : %A\n::> " State
            let json = JsonSerializer.Serialize(State)
            File.WriteAllText(Path.Combine(pathPrefix,TemporaryFilePath), json)
        with
            | e -> printf "%s" e.Message

    member private self.ReadInFile path =
        try
            let json = File.ReadAllText(path)
            State <- JsonSerializer.Deserialize<ConcurrentDictionary<int, string>>(json)
            self.HandleEips (Set.ofSeq State.Keys)
            self.Watch (Set.ofSeq State.Keys) 
            printf "Restore : %A\n::> " State
        with
            | _ -> ()

    member private _.RunEvery action (period : int) args= 
        let rec loop () =
            async {
                let _ = action args
                do! Async.Sleep (period * 1000)
                do! loop()
            }
        loop()

    member private _.CompareDiffs (oldState : ConcurrentDictionary<int, string>) (newState : ConcurrentDictionary<int, string>) eips =
        let loop eip = 
            let newHash = newState.ContainsKey eip
            let oldHash = oldState.ContainsKey eip
            if oldHash = newHash && oldHash = true 
            then oldState[eip] <> newState[eip]
            else false
        eips |> Set.filter loop

    member public self.Watch (eips:int Set) = 
        Flagged <- Set.union eips Flagged
        let newStateSegment = self.GetEipMetadata eips
        for kvp in newStateSegment do 
            State[kvp.Key] <- kvp.Value

    member public _.Unwatch eips = 
        Flagged <- Set.difference Flagged eips 
        eips |> Set.iter (fun eip -> ignore <| State.Remove(eip))
    
    member private self.GetEipMetadata eips : ConcurrentDictionary<int, string> =
        let GetEipFileData = self.GetRequestWithAuth Config.GitToken
        let newState = new ConcurrentDictionary<int, string>()
        do eips |> Set.iter (fun eip -> newState[eip] <- (GetEipFileData  eip).["sha"].AsString())
        newState

    member private self.HandleEips eips = 
        let eipData = self.GetEipMetadata eips 
        let changedEips = self.CompareDiffs State eipData eips
        State <- eipData
        match changedEips with 
        | _ when Set.isEmpty changedEips -> ()
        | _ -> 
            let metadata = changedEips |> Set.map (fun eip -> Metadata.FetchMetadata eip 0)
            printf "Changed EIPs : %A\n::> " (metadata)
            let results = 
                let rec flatten flatres res = 
                    match res with 
                    | [] -> flatres
                    | Ok h::t -> flatten (h::flatres) t 
                    | Error e::t -> flatten flatres t 
                        
                metadata
                |> List.ofSeq
                |> flatten []


            match Email with
            | User(email) -> 
                results
                |> Mail.NotifyEmail Config email

    member public self.Start period silosPath= 
        let thread = 
            new Thread(fun () -> 
                try 
                    self.ReadInFile silosPath
                    let actions = self.RunEvery (self.HandleEips) period Flagged
                    Async.RunSynchronously(actions, 0, CancellationToken.Token)
                with 
                | :? System.Exception -> printf "Stopped reading commands\n::> "
            )
        thread.Start()
