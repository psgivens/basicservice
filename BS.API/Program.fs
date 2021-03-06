module BS.API.App

open System
open System.IO
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Giraffe

open Microsoft.AspNetCore.Http
open FSharp.Control.Tasks.V2.ContextSensitive

open BS.API
open BS.API.Controllers

open Amazon.DynamoDBv2
open BS.Domain.DAL.EngagementEventDal
open BS.Domain.DAL.EventEnvelopeDal
open BS.Domain.DAL.DataAccess


let endpointDomain = "ddb-local"
let endpointPort = 8000
let endpoint = sprintf "http://%s:%d" endpointDomain endpointPort

printfn "  -- Setting up a DynamoDB-Local client (DynamoDB Local seems to be running)" 
let ddbConfig = AmazonDynamoDBConfig ( ServiceURL = endpoint )
printfn "doing the work"



// ---------------------------------
// Models
// ---------------------------------

type Message =
    {
        Text : string
    }

[<CLIMutable>]
type Car =
    {
        Name   : string
        Make   : string
        Wheels : int
    }

let submitCar : HttpHandler =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        task {
            // Binds a JSON payload to a Car object
            let! car = ctx.BindJsonAsync<Car>()

            // Sends the object back to the client
            return! Successful.OK car next ctx
        }



// ---------------------------------
// Views
// ---------------------------------

module Views =
    open GiraffeViewEngine

    let layout (content: XmlNode list) =
        html [] [
            head [] [
                title []  [ encodedText "BS.API" ]
                link [ _rel  "stylesheet"
                       _type "text/css"
                       _href "/main.css" ]
            ]
            body [] content
        ]

    let partial () =
        h1 [] [ encodedText "BS.API" ]

    let index (model : Message) =
        [
            partial()
            p [] [ encodedText model.Text ]
        ] |> layout

// ---------------------------------
// Web app
// ---------------------------------

let indexHandler (name : string) =
    let greetings = sprintf "Hello %s, from Giraffe!" name
    let model     = { Text = greetings }
    let view      = Views.index model
    htmlView view


let webApp =

    // Build dependencies
    let createClient () = new AmazonDynamoDBClient(ddbConfig)
    let eventConverters: IEventConverter list = [EngagementEventConverter ()] |> List.map (fun c -> c :> IEventConverter)
    let createEventEnvelopeDao = createEventEnvelopeFactory eventConverters
    let engagementController = EngagementController (createClient, createEventEnvelopeDao)

    choose [
        route "/engagements" >=>
            POST >=> engagementController.SubmitEngagement 
        GET >=>
            choose [
                route "/" >=> indexHandler "world"
                routef "/hello/%s" indexHandler
            ]
        POST >=> 
            route "/car" >=> submitCar            
        setStatusCode 404 >=> text "Not Found" ]

// ---------------------------------
// Error handler
// ---------------------------------

let errorHandler (ex : Exception) (logger : ILogger) =
    logger.LogError(ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

// ---------------------------------
// Config and Main
// ---------------------------------

let configureCors (builder : CorsPolicyBuilder) =
    builder
           .AllowAnyOrigin()
            // .WithOrigins("http://localhost:8080")
           .AllowAnyMethod()
           .AllowAnyHeader()
           |> ignore

let configureApp (app : IApplicationBuilder) =
    let env = app.ApplicationServices.GetService<IWebHostEnvironment>()
    (match env.EnvironmentName with
    | "Development" -> app.UseDeveloperExceptionPage()
    | _ -> app.UseGiraffeErrorHandler(errorHandler))
        // .UseHttpsRedirection()
        // .UseCors(configureCors)
        .UseStaticFiles()
        .UseGiraffe(webApp)

let configureServices (services : IServiceCollection) =
    // services.AddCors()    |> ignore
    services.AddGiraffe() |> ignore

let configureLogging (builder : ILoggingBuilder) =
    builder.AddFilter(fun l -> l.Equals LogLevel.Error)
           .AddConsole()
           .AddDebug() |> ignore

[<EntryPoint>]
let main args =
    let contentRoot = Directory.GetCurrentDirectory()
    let webRoot     = Path.Combine(contentRoot, "WebRoot")
    Host.CreateDefaultBuilder(args)
        .ConfigureWebHostDefaults(
            fun webHostBuilder ->
                webHostBuilder
                    .UseContentRoot(contentRoot)
                    .UseWebRoot(webRoot)
                    .Configure(Action<IApplicationBuilder> configureApp)
                    .ConfigureServices(configureServices)
                    .ConfigureLogging(configureLogging)
                    |> ignore)
        .Build()
        .Run()
    0