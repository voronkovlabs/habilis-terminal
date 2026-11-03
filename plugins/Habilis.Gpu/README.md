# GPU Monitor plugin

A GPU monitor like nvtop, in a Habilis Terminal panel (issue #70). Read-only.

## This computer (`gpu:`)

- The NVIDIA GPUs first: load, memory used / total, temperature, power draw / limit. Then the processes using them,
  with their GPU memory and PID.
- Enter on a GPU lists only its processes; `..` goes back.
- The panel samples `nvidia-smi` (installed with the NVIDIA driver) and refreshes itself every 2 seconds (setting).
  Shift+F4 pauses and resumes.
- Ctrl+Q in the other panel shows the GPU under the cursor: bars, charts of the last 60 samples (load, memory,
  temperature, power) and its processes. F3 shows the same text.
- Without an NVIDIA GPU or driver the panel says that nvidia-smi was not found.

## SageMaker (`gpu:sm`, `gpu:sm:profile/region`)

- The training jobs in progress, with their instances. Enter on a job lists its hosts (`algo-1`, `algo-2`...) with
  GPU, GPU memory, CPU and memory load from CloudWatch (`/aws/sagemaker/TrainingJobs`, dimension Host).
- GPU and CPU values are summed over the host's GPUs and cores, as SageMaker reports them: 4 GPUs at full load show
  400%. CloudWatch has one value per minute, about a minute late; the panel refreshes every 60 seconds (setting).
- Ctrl+Q shows the last 30 minutes of a host as charts.
- Permissions: `sagemaker:ListTrainingJobs`, `sagemaker:DescribeTrainingJob`, `cloudwatch:GetMetricData`. Profiles,
  credentials and errors work as in the other AWS plugins (standard AWS config and credentials files, role and SSO
  profiles; nothing is written anywhere).

## Settings

F9 > Options > Plugins > GPU Monitor: refresh intervals, AWS profile and region for the SageMaker view, request
timeout, AWS credentials file.

## Test plan

1. `gpu:` on a computer with an NVIDIA GPU: the GPU and its processes; start a GPU program (a game, `python` with
   CUDA) and watch the load and the process appear within a few seconds.
2. Ctrl+Q: the charts grow by one column per sample. Shift+F4: the values stop changing; again: they move.
3. Enter on the GPU: only its processes; `..` back.
4. `gpu:sm:<profile>/<region>` while a training job runs: the job, then its hosts; compare GPUUtilization with the
   CloudWatch console (namespace /aws/sagemaker/TrainingJobs, metric GPUUtilization, Host = `<job>/algo-1`).
5. With a profile lacking `cloudwatch:GetMetricData`: the error names the permission.
