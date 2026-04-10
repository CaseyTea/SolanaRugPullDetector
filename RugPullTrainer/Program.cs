using Microsoft.ML;
using RugPullShared;

string csvPath = "solrpds_data.csv";
string modelPath = "rugpull_model.zip";

var mlContext = new MLContext(seed: 42);

Console.WriteLine("Loading dataset...");
IDataView allData = mlContext.Data.LoadFromTextFile<RugPullData>(
    path: csvPath, hasHeader: true, separatorChar: ',');

var split = mlContext.Data.TrainTestSplit(allData, testFraction: 0.2, seed: 42);

var pipeline =
    mlContext.Transforms.CustomMapping(
        new InactivityMapping().GetMapping(),
        contractName: "InactivityToLabel")
    .Append(mlContext.Transforms.Concatenate("Features",
        nameof(RugPullData.TotalAddedLiquidity),
        nameof(RugPullData.TotalRemovedLiquidity),
        nameof(RugPullData.NumLiquidityAdds),
        nameof(RugPullData.NumLiquidityRemoves),
        nameof(RugPullData.AddToRemoveRatio)))
    .Append(mlContext.Transforms.NormalizeMinMax("Features"))
    .Append(mlContext.BinaryClassification.Trainers.FastTree(
        labelColumnName: "Label",
        featureColumnName: "Features",
        numberOfLeaves: 50,
        numberOfTrees: 300,
        minimumExampleCountPerLeaf: 10,
        learningRate: 0.1));

Console.WriteLine("Training...");
var sw = System.Diagnostics.Stopwatch.StartNew();
var model = pipeline.Fit(split.TrainSet);
sw.Stop();
Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F1}s\n");

var predictions = model.Transform(split.TestSet);
var metrics = mlContext.BinaryClassification.Evaluate(predictions, labelColumnName: "Label");

Console.WriteLine($"Accuracy:  {metrics.Accuracy:P2}");
Console.WriteLine($"AUC:       {metrics.AreaUnderRocCurve:P2}");
Console.WriteLine($"F1 Score:  {metrics.F1Score:P2}");
Console.WriteLine($"Precision: {metrics.PositivePrecision:P2}");
Console.WriteLine($"Recall:    {metrics.PositiveRecall:P2}");

mlContext.Model.Save(model, allData.Schema, modelPath);
Console.WriteLine($"\nModel saved to {modelPath} ({new FileInfo(modelPath).Length / 1024.0:F1} KB)");
