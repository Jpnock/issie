(*
    SimulationView.fs

    View for simulation in the right tab.
*)

module SimulationView

open Fulma
open Fable.React
open Fable.React.Props

open NumberHelpers
open Helpers
open JSHelpers
open DiagramStyle
open PopupView
open MemoryEditorView
open ModelType
open CommonTypes
open SimulatorTypes
open Extractor
open Simulator
open NumberHelpers

//----------------------------View level simulation helpers------------------------------------//

type SimCache = {
    Name: string
    StoredState: CanvasState
    StoredResult: Result<SimulationData, SimulationError>
    }



let simCacheInit name = {
    Name = name; 
    StoredState = ([],[]) 
    StoredResult = Ok {
        Graph = Map.empty
        Inputs = []
        Outputs = []
        IsSynchronous=false
        NumberBase = NumberBase.Hex
        ClockTickNumber = 0
        }
    }
        

let mutable simCache: SimCache = simCacheInit ""

let rec prepareSimulationMemoised
        (diagramName : string)
        (canvasState : JSCanvasState)
        (loadedDependencies : LoadedComponent list)
        : Result<SimulationData, SimulationError> * CanvasState =
    let rState = extractReducedState canvasState
    if diagramName <> simCache.Name then
        simCache <- simCacheInit diagramName
        prepareSimulationMemoised diagramName canvasState loadedDependencies
    else
        let isSame = rState = simCache.StoredState
        if  isSame then
            simCache.StoredResult, rState
        else
            let simResult = prepareSimulation diagramName rState loadedDependencies
            simCache <- {
                Name = diagramName
                StoredState = rState
                StoredResult = simResult
                }
            simResult, rState

    
    


/// Start simulating the current Diagram.
/// Return SimulationData that can be used to extend the simulation
/// as needed, or error if simulation fails
let makeSimData model =
    match model.Diagram.GetCanvasState(), model.CurrentProj with
    | None, _ -> None
    | _, None -> None
    | Some jsState, Some project ->
        let otherComponents = 
            project.LoadedComponents 
            |> List.filter (fun comp -> comp.Name <> project.OpenFileName)
        (jsState, otherComponents)
        ||> prepareSimulationMemoised project.OpenFileName
        |> Some


let changeBase dispatch numBase = numBase |> SetSimulationBase |> dispatch

/// A line that can be used for an input, an output, or a state.
let private splittedLine leftContent rightConent =
    Level.level [Level.Level.Props [Style [MarginBottom "10px"]]] [
        Level.left [] [
            Level.item [] [ leftContent ]
        ]
        Level.right [] [
            Level.item [] [ rightConent ]
        ]
    ]

let rec private intToBinary i =
    match i with
    | 0 | 1 -> string i
    | _ ->
        let bit = string (i % 2)
        (intToBinary (i / 2)) + bit    

/// Pretty print a label with its width.
let private makeIOLabel label width =
    let label = cropToLength 15 true label
    match width with
    | 1 -> label
    | w -> sprintf "%s (%d bits)" label w

let private viewSimulationInputs
        (numberBase : NumberBase)
        (simulationGraph : SimulationGraph)
        (inputs : (SimulationIO * WireData) list)
        dispatch =
    let makeInputLine ((ComponentId inputId, ComponentLabel inputLabel, width), wireData) =
        assertThat (List.length wireData = width)
        <| sprintf "Inconsistent wireData length in viewSimulationInput for %s: expcted %d but got %d" inputLabel width wireData.Length
        let valueHandle =
            match wireData with
            | [] -> failwith "what? Empty wireData while creating a line in simulation inputs."
            | [bit] ->
                // For simple bits, just have a Zero/One button.
                Button.button [
                    Button.Props [ simulationBitStyle ]
                    Button.Color IsPrimary
                    (match bit with Zero -> Button.IsOutlined | One -> Button.Color IsPrimary)
                    Button.IsHovered false
                    Button.OnClick (fun _ ->
                        let newBit = match bit with
                                     | Zero -> One
                                     | One -> Zero
                        feedSimulationInput simulationGraph
                                            (ComponentId inputId) [newBit]
                        |> SetSimulationGraph |> dispatch
                    )
                ] [ str <| bitToString bit ]
            | bits ->
                let defValue = viewNum numberBase <| convertWireDataToInt bits
                Input.text [
                    Input.Key (numberBase.ToString())
                    Input.DefaultValue defValue
                    Input.Props [
                        simulationNumberStyle
                        OnChange (getTextEventValue >> (fun text ->
                            match strToIntCheckWidth text width with
                            | Error err ->
                                let note = errorPropsNotification err
                                dispatch  <| SetSimulationNotification note
                            | Ok num ->
                                let bits = convertIntToWireData width num
                                // Close simulation notifications.
                                CloseSimulationNotification |> dispatch
                                // Feed input.
                                feedSimulationInput simulationGraph
                                                    (ComponentId inputId) bits
                                |> SetSimulationGraph |> dispatch
                        ))
                    ]
                ]
        splittedLine (str <| makeIOLabel inputLabel width) valueHandle
    div [] <| List.map makeInputLine inputs

let private staticBitButton bit =
    Button.button [
        Button.Props [ simulationBitStyle ]
        Button.Color IsPrimary
        (match bit with Zero -> Button.IsOutlined | One -> Button.Color IsPrimary)
        Button.IsHovered false
        Button.Disabled true
    ] [ str <| bitToString bit ]

let private staticNumberBox numBase bits =
    let width = List.length bits
    let value = viewFilledNum width numBase <| convertWireDataToInt bits
    Input.text [
        Input.IsReadOnly true
        Input.Value value
        Input.Props [simulationNumberStyle]
    ]

let private viewSimulationOutputs numBase (simOutputs : (SimulationIO * WireData) list) =
    let makeOutputLine ((ComponentId _, ComponentLabel outputLabel, width), wireData) =
        assertThat (List.length wireData = width)
        <| sprintf "Inconsistent wireData length in viewSimulationOutput for %s: expcted %d but got %d" outputLabel width wireData.Length
        let valueHandle =
            match wireData with
            | [] -> failwith "what? Empty wireData while creating a line in simulation output."
            | [bit] -> staticBitButton bit
            | bits -> staticNumberBox numBase bits
        splittedLine (str <| makeIOLabel outputLabel width) valueHandle
    div [] <| List.map makeOutputLine simOutputs

let private viewStatefulComponents comps numBase model dispatch =
    let getWithDefault (ComponentLabel lab) = if lab = "" then "no-label" else lab
    let makeStateLine (comp : SimulationComponent) =
        match comp.State with
        | DffState bit ->
            let label = sprintf "DFF: %s" <| getWithDefault comp.Label
            [ splittedLine (str label) (staticBitButton bit) ]
        | RegisterState bits ->
            let label = sprintf "Register: %s (%d bits)" (getWithDefault comp.Label) bits.Length
            [ splittedLine (str label) (staticNumberBox numBase bits) ]
        | RamState mem ->
            let label = sprintf "RAM: %s" <| getWithDefault comp.Label
            let initialMem compType = match compType with RAM m -> m | _ -> failwithf "what? viewStatefulComponents expected RAM component but got: %A" compType
            let viewDiffBtn =
                Button.button [
                    Button.Props [ simulationBitStyle ]
                    Button.Color IsPrimary
                    Button.OnClick (fun _ ->
                        openMemoryDiffViewer (initialMem comp.Type) mem model dispatch
                    )
                ] [ str "View" ]
            [ splittedLine (str label) viewDiffBtn ]
        | NoState -> []
    div [] ( List.collect makeStateLine comps )

let viewSimulationError (simError : SimulationError) =
    let error = 
        match simError.InDependency with
        | None ->
            div [] [
                str simError.Msg
                br []
                str <| "Please fix the error and retry."
            ]
        | Some dep ->
            div [] [
                str <| "Error found in dependency \"" + dep + "\":"
                br []
                str simError.Msg
                br []
                str <| "Please fix the error in the dependency and retry."
            ]
    div [] [
        Heading.h5 [ Heading.Props [ Style [ MarginTop "15px" ] ] ] [ str "Errors" ]
        error
    ]

let private generateTruthTable (simData : SimulationData) = 
    // TODO: support for multi-width inputs

    // 1) Store old state
    let originalInputState = (extractSimulationIOs simData.Inputs simData.Graph)

    let mutable simGraph = simData.Graph

    let mutable numInputs = 0

    // Contains a list of the first input
    let mutable inputMapping = []

    // 2) Set all inputs to 0
    for ((ComponentId inputId, ComponentLabel inputLabel, width), wireData) in originalInputState do
        simGraph <- match wireData with
                    | [] -> failwith "not implemented (zero input bits generating truth table)" 
                    | [bit] ->
                        inputMapping <- List.append inputMapping [numInputs]
                        numInputs <- numInputs + 1
                        match bit with
                        | Zero -> simGraph
                        | One -> feedSimulationInput simGraph (ComponentId inputId) [Zero]                   
                    | bits ->
                        inputMapping <- List.append inputMapping [numInputs]
                        numInputs <- numInputs + List.length bits
                        feedSimulationInput simGraph (ComponentId inputId) [ for a in 1 .. (List.length bits) do yield Zero ]



    let bitNumberToInputIndex row =
        let mutable index = 0
        let mutable found = -1
        let inputMappingLength = List.length inputMapping
        while found = -1 && index < inputMappingLength do
            found <- if inputMapping.[index] = row then index
                     else if inputMapping.[index] > row then index - 1
                     else -1                     
            index <- index + 1
        if found = -1 then (List.length inputMapping) - 1
        else found            

    // TODO: implement as grey code so this is quicker
    // 3) Between 0 to 2^num inputs - 1, calc the value of the outputs
    let doInputSim rowNum =
        let binRowNum = intToBinary rowNum

        let rec extendString s len =
            let strLen = String.length s
            match strLen with
            | x when x = len -> s
            | _ -> extendString ("0" + s) len

        let extendedBinRowNum = extendString binRowNum numInputs

        let setInput ((ComponentId inputId, ComponentLabel inputLabel, width), wireData) state offset = 
            let curState = match wireData with
                           | [bit] -> bit
                           | bits -> bits.[offset]
                           | _ -> failwith "not implemented (set input)"
            match curState with
            | Zero ->
                let mapping = List.mapi (fun i v -> if i = offset then state else v) wireData
                match state with
                | One -> feedSimulationInput simGraph (ComponentId inputId) mapping
                | _ -> simGraph
            | One ->
                let mapping = List.mapi (fun i v -> if i = offset then state else v) wireData
                match state with
                | Zero -> feedSimulationInput simGraph (ComponentId inputId) mapping
                | _ -> simGraph

        let mutable bitNum = 0
        for state in extendedBinRowNum do
            let inputStates = extractSimulationIOs simData.Inputs simGraph

            let mutable inputStatesList = []
            for input in inputStates do
                inputStatesList <- List.append inputStatesList [input]

            let inputIndex = bitNumberToInputIndex bitNum
            let input = inputStatesList.[inputIndex]
            let bitOffset = bitNum - inputMapping.[inputIndex]
            simGraph <- match state with
                        | '0' -> setInput input Zero bitOffset
                        | '1' -> setInput input One bitOffset
                        | _ -> failwith "not implemented (input zip)"
            bitNum <- bitNum + 1
        printf "return"
        simGraph

    let maxTruthTableValue = (pown 2 numInputs) - 1
    
    let simInputs = extractSimulationIOs simData.Inputs simGraph
    let simOutputs = extractSimulationIOs simData.Outputs simGraph

    let mutable header = "#"
    for ((ComponentId inputId, ComponentLabel label, width), wireData) in simInputs do
        match wireData with
        | [] -> failwith "not implemented (zero input bits generating truth table)" 
        | [bit] ->
            header <- header + "\t" + label             
        | bits ->
            for i in [0 .. (List.length bits - 1)] do
                header <- header + "\t" + sprintf "%c%d" (Seq.head label) i
    
    for ((ComponentId inputId, ComponentLabel label, width), wireData) in simOutputs do
        header <- header + "\t" + label

    let mutable rows = [ header; ]

    for i in [0 .. maxTruthTableValue] do
        let simResult = doInputSim i
        let mutable row = sprintf "%d" i

        let simInputs = extractSimulationIOs simData.Inputs simGraph
        let simOutputs = extractSimulationIOs simData.Outputs simGraph

        for ((ComponentId inputId, ComponentLabel inputLabel, width), wireData) in simInputs do
            for bit in wireData do
                let s = match bit with
                        | Zero -> "\t0"
                        | One -> "\t1"                
                row <- row + s

        for ((ComponentId inputId, ComponentLabel inputLabel, width), wireData) in simOutputs do
            let s = match wireData with
                    | bits ->
                        sprintf "%A" wireData
                    | [bit] ->
                        match bit with
                        | Zero -> "\t0"
                        | One -> "\t1"
                    | _ -> failwith "not implemented (simOutputs truth table sequence)"
            row <- row + s
        
        rows <- List.append rows [row]

    (simGraph, rows)

// TODO: remove this from global state
let mutable private truthTableRows = ""

let private viewSimulationData (simData : SimulationData) model dispatch =
    let hasMultiBitOutputs =
        simData.Outputs |> List.filter (fun (_,_,w) -> w > 1) |> List.isEmpty |> not
    let maybeBaseSelector =
        match hasMultiBitOutputs with
        | false -> div [] []
        | true -> baseSelector simData.NumberBase (changeBase dispatch)
    let maybeClockTickBtn =
        match simData.IsSynchronous with
        | false -> div [] []
        | true ->
            Button.button [
                Button.Color IsSuccess
                Button.OnClick (fun _ ->
                    if SimulationRunner.simTrace <> None then
                        printfn "*********************Incrementing clock from simulator button******************************"
                        printfn "-------------------------------------------------------------------------------------------"
                    feedClockTick simData.Graph |> SetSimulationGraph |> dispatch
                    if SimulationRunner.simTrace <> None then
                        printfn "-------------------------------------------------------------------------------------------"
                        printfn "*******************************************************************************************"
                    IncrementSimulationClockTick |> dispatch
                )
            ] [ str <| sprintf "Clock Tick %d" simData.ClockTickNumber ]
    let maybeStatefulComponents =
        let stateful = extractStatefulComponents simData.Graph
        match List.isEmpty stateful with
        | true -> div [] []
        | false -> div [] [
            Heading.h5 [ Heading.Props [ Style [ MarginTop "15px" ] ] ] [ str "Stateful components" ]
            viewStatefulComponents (extractStatefulComponents simData.Graph) simData.NumberBase model dispatch
        ]

    let getInputState ((ComponentId inputId, ComponentLabel inputLabel, width), wireData) =
        assertThat (List.length wireData = width)
        <| sprintf "Inconsistent wireData length in viewSimulationInput for %s: expcted %d but got %d" inputLabel width wireData.Length
        let valueHandle =
            match wireData with
            | [] -> failwith "what? Empty wireData while creating a line in simulation inputs."
            | [bit] ->
                match bit with
                    | Zero -> "0"
                    | One -> "1"
            | bits ->
                "NOT IMPLEMENTED FOR MULTI INPUT BITS"
           
        printf "%s %s" (makeIOLabel inputLabel width) valueHandle
        sprintf "%s %s" (makeIOLabel inputLabel width) valueHandle

    let maybeTruthTable = match true with
                            | false -> div [] []
                            | true -> div [] [
                                Textarea.textarea [
                                    Textarea.Value truthTableRows
                                    Textarea.IsReadOnly true
                                ] [
                                    
                                ]]

    div [] [
        splittedLine maybeBaseSelector maybeClockTickBtn

        Heading.h5 [ Heading.Props [ Style [ MarginTop "15px" ] ] ] [ str "Inputs" ]
        viewSimulationInputs
            simData.NumberBase
            simData.Graph
            (extractSimulationIOs simData.Inputs simData.Graph)
            dispatch

        Heading.h5 [ Heading.Props [ Style [ MarginTop "15px" ] ] ] [ str "Outputs" ]
        viewSimulationOutputs simData.NumberBase
        <| extractSimulationIOs simData.Outputs simData.Graph

        Heading.h5 [ Heading.Props [ Style [ MarginTop "15px" ] ] ] [ str "Truth Table" ]

        Button.button [
            Button.Color IsPrimary
            Button.IsHovered false
            Button.OnClick (fun _ ->
                        let (simGraph, table) = generateTruthTable simData
                        truthTableRows <- List.reduce (fun a b -> a + "\n" + b) table
                        simGraph |> SetSimulationGraph |> dispatch
                    )            
        ] [ str <| "Generate truth table" ]

        maybeTruthTable

        maybeStatefulComponents
    ]

  

let viewSimulation model dispatch =
    let JSState = model.Diagram.GetCanvasState ()
    let startSimulation () =
        match JSState, model.CurrentProj with
        | None, _ -> ()
        | _, None -> failwith "what? Cannot start a simulation without a project"
        | Some jsState, Some project ->
            let otherComponents =
                project.LoadedComponents
                |> List.filter (fun comp -> comp.Name <> project.OpenFileName)
            (jsState, otherComponents)
            ||> prepareSimulationMemoised project.OpenFileName
            |> function
               | Ok (simData), state -> Ok simData
               | Error simError, state ->
                  if simError.InDependency.IsNone then
                      // Highlight the affected components and connection only if
                      // the error is in the current diagram and not in a
                      // dependency.
                      (simError.ComponentsAffected, simError.ConnectionsAffected)
                      |> SetHighlighted |> dispatch
                  Error simError
            |> StartSimulation
            |> dispatch
    match model.CurrentStepSimulationStep with
    | None ->
        let simRes = makeSimData model
        let isSync = match simRes with | Some( Ok {IsSynchronous=true},_) | _ -> false
        let buttonColor, buttonText = 
            match simRes with
            | None -> IColor.IsWhite, ""
            | Some (Ok _, _) -> IsSuccess, "Start Simulation"
            | Some (Error _, _) -> IsWarning, "See Problems"
        div [] [
            str "Simulate simple logic using this tab."
            br []
            str (if isSync then "You can also use the Waveforms >> button to view waveforms" else "")
            br []; br []
            Button.button
                [ Button.Color buttonColor; Button.OnClick (fun _ -> startSimulation()) ]
                [ str buttonText ]
        ]
    | Some sim ->
        let body = match sim with
                   | Error simError -> viewSimulationError simError
                   | Ok simData -> viewSimulationData simData model dispatch
        let endSimulation _ =
            dispatch CloseSimulationNotification // Close error notifications.
            dispatch <| SetHighlighted ([], []) // Remove highlights.
            dispatch EndSimulation // End simulation.
            dispatch <| (JSDiagramMsg << InferWidths) () // Repaint connections.
        div [] [
            Button.button
                [ Button.Color IsDanger; Button.OnClick endSimulation ]
                [ str "End simulation" ]
            br []; br []
            str "The simulation uses the diagram as it was at the moment of
                 pressing the \"Start simulation\" button."
            hr []
            body
        ]
