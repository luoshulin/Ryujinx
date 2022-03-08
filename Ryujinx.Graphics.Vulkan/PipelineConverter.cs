﻿using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;

namespace Ryujinx.Graphics.Vulkan
{
    static class PipelineConverter
    {
        public static unsafe DisposableRenderPass ToRenderPass(this ProgramPipelineState state, VulkanGraphicsDevice gd, Device device)
        {
            const int MaxAttachments = Constants.MaxRenderTargets + 1;

            AttachmentDescription[] attachmentDescs = null;

            var subpass = new SubpassDescription()
            {
                PipelineBindPoint = PipelineBindPoint.Graphics
            };

            AttachmentReference* attachmentReferences = stackalloc AttachmentReference[MaxAttachments];

            Span<int> attachmentIndices = stackalloc int[MaxAttachments];
            Span<Silk.NET.Vulkan.Format> attachmentFormats = stackalloc Silk.NET.Vulkan.Format[MaxAttachments];

            int attachmentCount = 0;
            int colorCount = 0;
            int maxColorAttachmentIndex = 0;

            for (int i = 0; i < state.AttachmentEnable.Length; i++)
            {
                if (state.AttachmentEnable[i])
                {
                    maxColorAttachmentIndex = i;

                    attachmentFormats[attachmentCount] = gd.FormatCapabilities.ConvertToVkFormat(state.AttachmentFormats[i]);

                    attachmentIndices[attachmentCount++] = i;
                    colorCount++;
                }
            }

            if (state.DepthStencilEnable)
            {
                attachmentFormats[attachmentCount++] = gd.FormatCapabilities.ConvertToVkFormat(state.DepthStencilFormat);
            }

            if (attachmentCount != 0)
            {
                attachmentDescs = new AttachmentDescription[attachmentCount];

                for (int i = 0; i < attachmentCount; i++)
                {
                    int bindIndex = attachmentIndices[i];

                    var format = state.AttachmentFormats[i];

                    attachmentDescs[i] = new AttachmentDescription(
                        0,
                        attachmentFormats[i],
                        SampleCountFlags.SampleCount1Bit,
                        AttachmentLoadOp.Load,
                        AttachmentStoreOp.Store,
                        AttachmentLoadOp.Load,
                        AttachmentStoreOp.Store,
                        ImageLayout.General,
                        ImageLayout.General);
                }

                int colorAttachmentsCount = colorCount;

                if (colorAttachmentsCount > MaxAttachments - 1)
                {
                    colorAttachmentsCount = MaxAttachments - 1;
                }

                if (colorAttachmentsCount != 0)
                {
                    int maxAttachmentIndex = Constants.MaxRenderTargets - 1;
                    subpass.ColorAttachmentCount = (uint)maxAttachmentIndex + 1;
                    subpass.PColorAttachments = &attachmentReferences[0];

                    // Fill with VK_ATTACHMENT_UNUSED to cover any gaps.
                    for (int i = 0; i <= maxAttachmentIndex; i++)
                    {
                        subpass.PColorAttachments[i] = new AttachmentReference(Vk.AttachmentUnused, ImageLayout.Undefined);
                    }

                    for (int i = 0; i < colorAttachmentsCount; i++)
                    {
                        int bindIndex = attachmentIndices[i];

                        subpass.PColorAttachments[bindIndex] = new AttachmentReference((uint)i, ImageLayout.General);
                    }
                }

                if (state.DepthStencilEnable)
                {
                    uint dsIndex = (uint)attachmentCount - 1;

                    subpass.PDepthStencilAttachment = &attachmentReferences[MaxAttachments - 1];
                    *subpass.PDepthStencilAttachment = new AttachmentReference(dsIndex, ImageLayout.General);
                }
            }

            var subpassDependency = new SubpassDependency(
                0,
                0,
                PipelineStageFlags.PipelineStageAllGraphicsBit,
                PipelineStageFlags.PipelineStageAllGraphicsBit,
                AccessFlags.AccessMemoryReadBit | AccessFlags.AccessMemoryWriteBit,
                AccessFlags.AccessMemoryReadBit | AccessFlags.AccessMemoryWriteBit,
                0);

            fixed (AttachmentDescription* pAttachmentDescs = attachmentDescs)
            {
                var renderPassCreateInfo = new RenderPassCreateInfo()
                {
                    SType = StructureType.RenderPassCreateInfo,
                    PAttachments = pAttachmentDescs,
                    AttachmentCount = attachmentDescs != null ? (uint)attachmentDescs.Length : 0,
                    PSubpasses = &subpass,
                    SubpassCount = 1,
                    PDependencies = &subpassDependency,
                    DependencyCount = 1
                };

                gd.Api.CreateRenderPass(device, renderPassCreateInfo, null, out var renderPass).ThrowOnError();

                return new DisposableRenderPass(gd.Api, device, renderPass);
            }
        }

        public static unsafe PipelineState ToVulkanPipelineState(this ProgramPipelineState state, VulkanGraphicsDevice gd)
        {
            PipelineState pipeline = new PipelineState();
            pipeline.Initialize();

            // It is assumed that Dynamic State is enabled when this conversion is used.

            pipeline.BlendConstantA = state.BlendDescriptors[0].BlendConstant.Alpha;
            pipeline.BlendConstantB = state.BlendDescriptors[0].BlendConstant.Blue;
            pipeline.BlendConstantG = state.BlendDescriptors[0].BlendConstant.Green;
            pipeline.BlendConstantR = state.BlendDescriptors[0].BlendConstant.Red;

            pipeline.CullMode = state.CullEnable ? state.CullMode.Convert() : CullModeFlags.CullModeNone;

            pipeline.DepthBoundsTestEnable = false; // Not implemented.

            pipeline.DepthClampEnable = state.DepthClampEnable;

            pipeline.DepthTestEnable = state.DepthTest.TestEnable;
            pipeline.DepthWriteEnable = state.DepthTest.WriteEnable;
            pipeline.DepthCompareOp = state.DepthTest.Func.Convert();

            pipeline.FrontFace = state.FrontFace.Convert();

            pipeline.HasDepthStencil = state.DepthStencilEnable;
            pipeline.LineWidth = state.LineWidth;
            pipeline.LogicOpEnable = state.LogicOpEnable;
            pipeline.LogicOp = state.LogicOp.Convert();

            pipeline.MinDepthBounds = 0f; // Not implemented.
            pipeline.MaxDepthBounds = 0f; // Not implemented.

            pipeline.PatchControlPoints = state.PatchControlPoints;
            pipeline.PolygonMode = Silk.NET.Vulkan.PolygonMode.Fill; // Not implemented.
            pipeline.PrimitiveRestartEnable = state.PrimitiveRestartEnable;
            pipeline.RasterizerDiscardEnable = state.RasterizerDiscard;
            pipeline.SamplesCount = (uint)state.SamplesCount;
            pipeline.ScissorsCount = 16;
            pipeline.ViewportsCount = 16;

            pipeline.DepthBiasEnable = state.BiasEnable != 0;

            // Stencil masks and ref are dynamic, so are 0 in the Vulkan pipeline.

            pipeline.StencilFrontFailOp = state.StencilTest.FrontSFail.Convert();
            pipeline.StencilFrontPassOp = state.StencilTest.FrontDpPass.Convert();
            pipeline.StencilFrontDepthFailOp = state.StencilTest.FrontDpFail.Convert();
            pipeline.StencilFrontCompareOp = state.StencilTest.FrontFunc.Convert();
            pipeline.StencilFrontCompareMask = 0;
            pipeline.StencilFrontWriteMask = 0;
            pipeline.StencilFrontReference = 0;

            pipeline.StencilBackFailOp = state.StencilTest.BackSFail.Convert();
            pipeline.StencilBackPassOp = state.StencilTest.BackDpPass.Convert();
            pipeline.StencilBackDepthFailOp = state.StencilTest.BackDpFail.Convert();
            pipeline.StencilBackCompareOp = state.StencilTest.BackFunc.Convert();
            pipeline.StencilBackCompareMask = 0;
            pipeline.StencilBackWriteMask = 0;
            pipeline.StencilBackReference = 0;

            pipeline.StencilTestEnable = state.StencilTest.TestEnable;

            pipeline.Topology = state.Topology.Convert();

            int vaCount = Math.Min(Constants.MaxVertexAttributes, state.VertexAttribCount);

            for (int i = 0; i < vaCount; i++)
            {
                var attribute = state.VertexAttribs[i];
                var bufferIndex = attribute.IsZero ? 0 : attribute.BufferIndex + 1;

                pipeline.Internal.VertexAttributeDescriptions[i] = new VertexInputAttributeDescription(
                    (uint)i,
                    (uint)bufferIndex,
                    FormatTable.GetFormat(attribute.Format),
                    (uint)attribute.Offset);
            }

            int descriptorIndex = 1;
            pipeline.Internal.VertexBindingDescriptions[0] = new VertexInputBindingDescription(0, 0, VertexInputRate.Vertex);

            int vbCount = Math.Min(Constants.MaxVertexBuffers, state.VertexBufferCount);

            for (int i = 0; i < vbCount; i++)
            {
                var vertexBuffer = state.VertexBuffers[i];

                if (vertexBuffer.Enable)
                {
                    var inputRate = vertexBuffer.Divisor != 0 ? VertexInputRate.Instance : VertexInputRate.Vertex;

                    // TODO: Support divisor > 1
                    pipeline.Internal.VertexBindingDescriptions[descriptorIndex++] = new VertexInputBindingDescription(
                        (uint)i + 1,
                        (uint)vertexBuffer.Stride,
                        inputRate);
                }
            }

            pipeline.VertexBindingDescriptionsCount = (uint)descriptorIndex;

            // NOTE: Viewports, Scissors are dynamic.

            for (int i = 0; i < 8; i++)
            {
                var blend = state.BlendDescriptors[i];

                pipeline.Internal.ColorBlendAttachmentState[i] = new PipelineColorBlendAttachmentState(
                    blend.Enable,
                    blend.ColorSrcFactor.Convert(),
                    blend.ColorDstFactor.Convert(),
                    blend.ColorOp.Convert(),
                    blend.AlphaSrcFactor.Convert(),
                    blend.AlphaDstFactor.Convert(),
                    blend.AlphaOp.Convert(),
                    (ColorComponentFlags)state.ColorWriteMask[i]);
            }

            int maxAttachmentIndex = 0;
            for (int i = 0; i < 8; i++)
            {
                if (state.AttachmentEnable[i])
                {
                    pipeline.Internal.AttachmentFormats[maxAttachmentIndex++] = gd.FormatCapabilities.ConvertToVkFormat(state.AttachmentFormats[i]);
                }
            }

            if (state.DepthStencilEnable)
            {
                pipeline.Internal.AttachmentFormats[maxAttachmentIndex++] = gd.FormatCapabilities.ConvertToVkFormat(state.DepthStencilFormat);
            }

            pipeline.ColorBlendAttachmentStateCount = 8;
            pipeline.VertexAttributeDescriptionsCount = (uint)Math.Min(Constants.MaxVertexAttributes, state.VertexAttribCount);

            return pipeline;
        }
    }
}